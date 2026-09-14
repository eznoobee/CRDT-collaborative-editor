using Crdt.Core;
using Editor.Api.Infrastructure;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Tickets;
using Microsoft.AspNetCore.SignalR;

namespace Editor.Api.Hubs;

/// <summary>The collaborative editing hub (PROJECT_SPEC.md §7).</summary>
/// <remarks>
/// Authentication here is the connect ticket, not the OIDC token: §7 puts the
/// token nowhere near the URL, and the hub's handshake is a URL. The ticket is
/// redeemed once, at connect, and the binding it carried is what every
/// subsequent call is checked against.
/// </remarks>
public sealed partial class EditorHub : Hub
{
    private const string BindingKey = "editor.binding";

    /// <summary>The client method a fanned-out batch arrives on.</summary>
    public const string Broadcast = "ReceiveOperations";

    private readonly CatchUpReader _catchUp;
    private readonly DocumentBackplane _backplane;
    private readonly IReplicaClaims _claims;
    private readonly DocumentBroadcaster _broadcaster;
    private readonly DocumentConnections _connections;
    private readonly IConnectTicketStore _tickets;
    private readonly IUserConnections _userConnections;
    private readonly IServiceScopeFactory _scopes;
    private readonly IDocumentRoles _roles;
    private readonly IngestValidator _validator;
    private readonly IOperationRateLimiter _rateLimits;
    private readonly DocumentIngestState _state;
    private readonly OperationLogBatcher _log;
    private readonly ILogger<EditorHub> _logger;
    private readonly EditorMetrics _metrics;

    public EditorHub(
        CatchUpReader catchUp,
        DocumentBackplane backplane,
        IReplicaClaims claims,
        DocumentBroadcaster broadcaster,
        DocumentConnections connections,
        IConnectTicketStore tickets,
        IUserConnections userConnections,
        IServiceScopeFactory scopes,
        IDocumentRoles roles,
        IngestValidator validator,
        IOperationRateLimiter rateLimits,
        DocumentIngestState state,
        OperationLogBatcher log,
        ILogger<EditorHub> logger,
        EditorMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(catchUp);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(backplane);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(userConnections);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(rateLimits);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logger);

        _catchUp = catchUp;
        _backplane = backplane;
        _claims = claims;
        _broadcaster = broadcaster;
        _connections = connections;
        _tickets = tickets;
        _userConnections = userConnections;
        _scopes = scopes;
        _roles = roles;
        _validator = validator;
        _rateLimits = rateLimits;
        _state = state;
        _log = log;
        _logger = logger;
        _metrics = metrics;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetBinding(out var binding))
        {
            // §7: released before anything else, so the owner can resume this
            // replica immediately rather than waiting out its TTL. Compare-and-
            // delete against this session's token, so a connection that already
            // lost the claim cannot delete the one that replaced it.
            await _claims
                .ReleaseAsync(
                    binding.DocumentId, binding.ReplicaId, binding.ClaimToken, CancellationToken.None)
                .ConfigureAwait(false);

            // §7's per-user connection slot, given back here rather than left to
            // age out. The stale window exists for instances that die, not for
            // connections that close normally: a user who closes a tab and opens
            // another would otherwise be charged for both for a minute.
            //
            // CancellationToken.None on purpose, like the claim above. This runs
            // while the connection is being torn down, and a cancelled release
            // is a slot held until it expires.
            await _userConnections
                .ReleaseAsync(binding.UserId, binding.ReplicaId, CancellationToken.None)
                .ConfigureAwait(false);

            if (_connections.Remove(binding.DocumentId, Context.ConnectionId))
            {
                // The last connection this instance held for the document.
                // Staying subscribed would mean decoding traffic for a document
                // nobody here is reading.
                await _backplane.LeaveAsync(binding.DocumentId).ConfigureAwait(false);
            }
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var ticket = http?.Request.Query["access_token"].ToString();

        var binding = string.IsNullOrEmpty(ticket)
            ? null
            : await _tickets.RedeemAsync(ticket, Context.ConnectionAborted).ConfigureAwait(false);

        if (binding is null)
        {
            // Thrown, not Context.Abort(). Abort closes the connection after
            // the handshake has already completed, so the client's StartAsync
            // succeeds and it believes it is connected — a refusal the client
            // cannot tell from success is not a refusal. Throwing fails the
            // connection attempt itself.
            //
            // No detail, and no distinction between absent, expired, already
            // redeemed and forged. Each of those is a different fact about
            // someone else's session.
            throw new HubException(HubErrors.Unauthenticated);
        }

        Context.Items[BindingKey] = binding.Value;
        _connections.Add(
            binding.Value.DocumentId,
            Context.ConnectionId,
            binding.Value.UserId,
            binding.Value.ReplicaId,
            binding.Value.ClaimToken,
            Context.Abort);

        // Taken before the connection is considered joined, so a batch
        // published between here and the first send is not one this instance
        // was not yet listening for.
        await _backplane.JoinAsync(binding.Value.DocumentId).ConfigureAwait(false);

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(binding.Value.DocumentId))
            .ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Submits a batch of operations into the bound document.</summary>
    /// <remarks>
    /// Authorize, validate, append. Broadcast is not here: fanning a batch out
    /// to other connections is causal delivery's problem and lands with it.
    /// </remarks>
    public async Task<SubmitResult> SubmitAsync(OperationBatchMessage batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        // Opened before the first check that can refuse, so a submission that
        // is rejected still produces a trace. "Where did it stop" is the
        // question a trace is for, and a root started after the §7 checks would
        // answer it for exactly the submissions that succeeded.
        using var submit = EditorTracing.StartSubmit();

        // §10 puts the correlation id on every log line AND every span, and the
        // second half is what makes them one record: without it, a trace
        // showing a slow persist and the log lines from that connection are two
        // artefacts nobody can join. The hub's correlation id is the connection
        // id (Correlation.HubPath), so this is the same value the filter scopes
        // its lines with rather than a second identifier.
        submit?.SetTag(Logging.Correlation.Key, Context.ConnectionId);

        if (!TryGetBinding(out var binding))
        {
            Reject(HubErrors.Unauthenticated);
            return SubmitResult.Rejected(HubErrors.Unauthenticated);
        }

        // Check one: a field comparison against the binding, no I/O. §7 puts it
        // first because it costs nothing and stops a client submitting into a
        // document it never joined.
        if (batch.DocumentId != binding.DocumentId)
        {
            // not_found, not forbidden: the caller may have no idea whether
            // that document exists, and this answer must not tell them.
            Reject(HubErrors.NotFound);
            return SubmitResult.Rejected(HubErrors.NotFound);
        }

        // §7's other in-memory check. A client that could name another live
        // replica's id would author operations attributed to it, and every
        // replica would converge on the forgery.
        if (batch.ReplicaId != binding.ReplicaId)
        {
            Reject(HubErrors.Forbidden);
            return SubmitResult.Rejected(HubErrors.Forbidden);
        }

        // Check two: the role, cached with a five-second bound. Read on every
        // call rather than trusted from connect time, because a membership
        // revoked mid-session has to take effect mid-session.
        var role = await _roles
            .GetRoleAsync(binding.DocumentId, binding.UserId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (role is null)
        {
            Reject(HubErrors.NotFound);
            return SubmitResult.Rejected(HubErrors.NotFound);
        }

        if (role == Role.Viewer)
        {
            // §7: rejected and logged. The document id is safe to log — the
            // caller can see the document — and the ticket is not here at all.
            Log.ViewerWriteRejected(_logger, binding.DocumentId, binding.ReplicaId);
            Reject(HubErrors.Forbidden);
            return SubmitResult.Rejected(HubErrors.Forbidden);
        }

        var replicaId = ReplicaIdConversion.FromGuid(binding.ReplicaId);
        var arrived = System.Diagnostics.Stopwatch.GetTimestamp();

        IngestResult validated;
        using (EditorTracing.StartStage(EditorTracing.Validate))
        {
            validated = await _validator
                .ValidateAsync(binding.DocumentId, replicaId, batch.Operations, Context.ConnectionAborted)
                .ConfigureAwait(false);
        }

        if (validated.Rejection is not null)
        {
            Reject(validated.Rejection);
            return SubmitResult.Rejected(validated.Rejection);
        }

        var operations = validated.Operations!;

        // Counted after validation, where the expanded operation count exists:
        // a run arrives as one encoded operation and becomes many (3.6), and a
        // received count taken before expansion would under-report exactly the
        // traffic §7's limits exist for.
        _metrics.OperationsReceived.Add(operations.Count);
        if (operations.Count == 0)
        {
            return SubmitResult.Ok(0);
        }

        // §7's abuse limits, charged in code points rather than messages.
        // After validation because that is where the batch's expanded
        // operation count exists — runs are expanded on ingest (3.6), so one
        // 256-code-point run costs what 256 single inserts cost, which is the
        // bypass §7 names. Before the append, so an over-budget caller does
        // not write.
        var budget = await _rateLimits
            .ChargeAsync(binding.UserId, Context.ConnectionId, operations.Count, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (!budget.Allowed)
        {
            Log.RateLimited(_logger, binding.DocumentId, operations.Count);
            Reject(IngestRejection.RateLimited);
            return SubmitResult.Throttled(IngestRejection.RateLimited, budget.RetryAfter);
        }

        AppendResult appended;
        using (EditorTracing.StartStage(EditorTracing.Persist))
        {
            appended = await _log.SubmitAsync(binding.DocumentId, operations).ConfigureAwait(false);
        }

        // Only after the append. Advancing the expected sequence for a batch
        // that failed to write would reject the client's retry of the very
        // operations the server does not have.
        _state.Accepted(binding.DocumentId, operations);

        // Fanned out after the write, never before: a client that received an
        // operation the server then failed to persist would hold state no
        // amount of reconnecting could recover, because catch-up reads the log.
        //
        // Sent per connection rather than to the SignalR group, because §8
        // requires a slow client to be dropped rather than waited for and a
        // group send exposes no per-connection timeout. The sender is excluded
        // because it already has these operations — an optimisation, not a
        // correctness requirement, since §5 makes re-delivery harmless.
        var message = new OperationBroadcast(
            binding.DocumentId, batch.Operations, appended.HighestServerSeq);

        using (EditorTracing.StartStage(EditorTracing.Broadcast))
        {
            await _broadcaster.FanOutAsync(
            _connections.Others(binding.DocumentId, Context.ConnectionId),
            (connection, token) => Clients.Client(connection).SendAsync(Broadcast, message, token),
            connection =>
            {
                // §8: dropped to catch-up, which means closed. A client that is
                // silently starved renders a document that is wrong without
                // knowing; a closed connection is something it can act on, and
                // its resync path is catch-up by version vector.
                Log.BackpressureDrop(_logger, binding.DocumentId, connection);
                _connections.Abort(binding.DocumentId, connection);
                return Task.CompletedTask;
            },
                Context.ConnectionAborted).ConfigureAwait(false);

            // And then the instances this one cannot see. §8 forbids sticky
            // sessions, so the other people editing this document are routinely
            // connected somewhere else; the fan-out above reaches only the
            // connections this process happens to hold.
            await _backplane.PublishAsync(message).ConfigureAwait(false);
        }

        _metrics.OperationsApplied.Add(operations.Count);

        // §8's segment: arrival to broadcast enqueue, which is the span the
        // p99 target names. Measured here rather than around the whole method
        // so that a change to what happens after enqueue cannot silently move
        // the number the target is judged against (3b.1's lesson).
        _metrics.PropagationLatency.Record(
            System.Diagnostics.Stopwatch.GetElapsedTime(arrived).TotalMilliseconds);

        return SubmitResult.Ok(operations.Count);
    }

    /// <summary>
    /// Records what this replica holds, so the stability frontier can move
    /// (PROJECT_SPEC.md §5).
    /// </summary>
    /// <param name="known">
    /// The version vector below which this client holds every operation, with
    /// no gaps. A prefix, never a maximum: §8 makes broadcast unordered, and
    /// reporting the highest thing seen would mark stable an operation someone
    /// is still missing.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>A method of its own, and that is the point of it.</strong> The
    /// same vector is recorded on catch-up and on every submission, and both of
    /// those are attached to <em>doing</em> something. A viewer submits nothing
    /// and catches up once, so under those two paths alone its acknowledgement
    /// freezes at the moment it connected — and one person reading a document
    /// holds the frontier still for as long as their tab is open, with no error
    /// anywhere and GC quietly reclaiming nothing. §13.32, third occurrence: a
    /// signal attached to an action covers only the principals who take it.
    /// </para><para>
    /// Answered with nothing. A client that cannot tell whether its
    /// acknowledgement landed loses nothing by it — the next one supersedes it,
    /// and the frontier only ever moves forward.
    /// </para>
    /// </remarks>
    public async Task AcknowledgeAsync(Dictionary<Guid, long> known)
    {
        ArgumentNullException.ThrowIfNull(known);

        if (!TryGetBinding(out var binding))
        {
            return;
        }

        // No role check, deliberately, and it is worth saying why rather than
        // leaving it to be noticed. This reports what the caller already holds
        // and returns nothing; a revoked member calling it learns nothing they
        // did not have and changes nothing they could not already change by
        // holding their socket open. The membership sweep is what closes that
        // socket (§7).
        await RecordAcknowledgementAsync(binding, known, AcknowledgedByTimer).ConfigureAwait(false);
    }

    /// <summary>§5's periodic acknowledgement, from the client's own timer.</summary>
    private const string AcknowledgedByTimer = "timer";

    /// <summary>The acknowledgement piggybacked on a catch-up.</summary>
    private const string AcknowledgedByCatchUp = "catchup";

    /// <summary>Writes an acknowledgement through a scoped context.</summary>
    /// <param name="binding">Whose connection, and which replica, is reporting.</param>
    /// <param name="known">The version vector the client says it holds, next-expected.</param>
    /// <param name="via">
    /// What prompted it. Carried into the metric rather than derived there,
    /// because the two callers are the whole diagnostic value: a client whose
    /// timer has stopped still catches up once per connection, so an untagged
    /// count stays healthy while the frontier stops moving.
    /// </param>
    private async Task RecordAcknowledgementAsync(
        ConnectionBinding binding, Dictionary<Guid, long> known, string via)
    {
        if (known.Count == 0)
        {
            return;
        }

        foreach (var next in known.Values)
        {
            // A negative sequence would be stored and then compared as a
            // minimum, dragging the frontier below zero and marking nothing
            // stable forever. Refused silently: this method has no answer, and
            // a malformed acknowledgement from a client that is otherwise
            // working is not worth closing a connection over.
            if (next < 0)
            {
                return;
            }
        }

        // NEXT EXPECTED on the wire and next expected in the frontier, stored
        // as it arrives. There was a conversion here to highest-held form, and
        // it was wrong at zero: a vector saying "next is 0" means this replica
        // holds nothing from that author, which highest-held cannot express —
        // dropping the entry reads back as zero from the minimum, and zero in
        // that form claims (author, 0) is held. §5 has the argument; the point
        // for this method is that storing what the client sent, unmodified, is
        // what removes the failure rather than a check that catches it.
        await using var scope = _scopes.CreateAsyncScope();
        var frontier = scope.ServiceProvider.GetRequiredService<IStabilityFrontier>();

        await frontier
            .AcknowledgeAsync(binding.DocumentId, binding.ReplicaId, known, CancellationToken.None)
            .ConfigureAwait(false);

        // After the write, not before: a count that moves for an acknowledgement
        // that failed to store reports a frontier advancing on evidence the
        // database does not have.
        _metrics.Acknowledgements.Add(1, new KeyValuePair<string, object?>("via", via));
    }

    /// <summary>
    /// What this connection has missed, given what it already has.
    /// </summary>
    /// <param name="known">
    /// Per replica id, the next sequence number this client expects — its
    /// version vector.
    /// </param>
    /// <param name="forceSnapshot">
    /// Skips the delta path. Exists so §13.14's rule can be honoured: the
    /// snapshot floor is exercised on its own, because a fallback that only
    /// ever runs behind a working fast path is a fallback nobody has tested.
    /// A client may set it after losing local state.
    /// </param>
    /// <remarks>
    /// The cursor is the version vector, never <c>server_seq</c>. §8 makes
    /// broadcast unordered, so a client can hold 105 without holding 100, and a
    /// server_seq watermark would silently skip whatever fell in the gap.
    /// </remarks>
    public async Task<CatchUpResult> CatchUpAsync(
        Dictionary<Guid, long> known, bool forceSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(known);

        if (!TryGetBinding(out var binding))
        {
            return CatchUpResult.Rejected(HubErrors.Unauthenticated);
        }

        // The same two-tier check every other call gets. Catch-up returns the
        // whole document, so skipping authorization here would be a way to read
        // one without ever submitting to it.
        var role = await _roles
            .GetRoleAsync(binding.DocumentId, binding.UserId, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (role is null)
        {
            return CatchUpResult.Rejected(HubErrors.NotFound);
        }

        var vector = new Dictionary<ReplicaId, ulong>();
        foreach (var (replica, next) in known)
        {
            if (next < 0)
            {
                return CatchUpResult.Rejected(IngestRejection.Malformed);
            }

            vector[ReplicaIdConversion.FromGuid(replica)] = (ulong)next;
        }

        // Piggybacked: a client asking to catch up has just told us exactly
        // what it holds, and recording it costs one write it was already
        // paying for. Not sufficient on its own — a viewer catches up once —
        // which is what AcknowledgeAsync exists for.
        await RecordAcknowledgementAsync(binding, known, AcknowledgedByCatchUp).ConfigureAwait(false);

        var caught = await _catchUp
            .ReadAsync(binding.DocumentId, vector, forceSnapshot, Context.ConnectionAborted)
            .ConfigureAwait(false);

        _metrics.CatchUps.Add(
            1,
            new KeyValuePair<string, object?>(
                "source", caught.Snapshot is null ? "log" : "snapshot"));

        return new CatchUpResult(null, caught.Snapshot, caught.Operations, caught.ServerSeq);
    }

    /// <summary>
    /// Records a refusal against §10's counters, tagged with its code.
    /// </summary>
    /// <remarks>
    /// One call site per refusal rather than a wrapper around the method,
    /// because the codes are the point: an untagged rejection count tells an
    /// operator that something is being refused and nothing about what, which
    /// is the difference between a dashboard that diagnoses and one that exists
    /// (§13.22). <c>resync_required</c> also gets its own counter — §9 makes it
    /// the one refusal that destroys a user's unsent work, so its rate is not a
    /// row in a breakdown, it is a thing to alert on.
    /// </remarks>
    private void Reject(string code)
    {
        _metrics.OperationsRejected.Add(1, new KeyValuePair<string, object?>("code", code));
        System.Diagnostics.Activity.Current.Rejected(code);

        if (code == IngestRejection.ResyncRequired)
        {
            _metrics.ResyncRequired.Add(1);
        }
    }

    /// <summary>The SignalR group carrying one document's broadcasts.</summary>
    public static string Group(Guid documentId) => $"document:{documentId:N}";

    private bool TryGetBinding(out ConnectionBinding binding)
    {
        if (Context.Items.TryGetValue(BindingKey, out var stored) && stored is ConnectionBinding found)
        {
            binding = found;
            return true;
        }

        binding = default;
        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3402,
            Level = LogLevel.Warning,
            Message = "Dropped connection {ConnectionId} on document {DocumentId} for backpressure.")]
        public static partial void BackpressureDrop(ILogger logger, Guid documentId, string connectionId);

        [LoggerMessage(
            EventId = 3403,
            Level = LogLevel.Warning,
            Message = "Refused {CodePoints} code points on document {DocumentId}: over §7's rate limit.")]
        public static partial void RateLimited(ILogger logger, Guid documentId, int codePoints);

        [LoggerMessage(
            EventId = 3401,
            Level = LogLevel.Warning,
            Message = "Viewer write rejected on document {DocumentId} from replica {ReplicaId}.")]
        public static partial void ViewerWriteRejected(ILogger logger, Guid documentId, Guid replicaId);
    }
}
