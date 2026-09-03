using Crdt.Core;
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
    private readonly DocumentBroadcaster _broadcaster;
    private readonly DocumentConnections _connections;
    private readonly IConnectTicketStore _tickets;
    private readonly IDocumentRoles _roles;
    private readonly IngestValidator _validator;
    private readonly DocumentIngestState _state;
    private readonly OperationLogBatcher _log;
    private readonly ILogger<EditorHub> _logger;

    public EditorHub(
        CatchUpReader catchUp,
        DocumentBroadcaster broadcaster,
        DocumentConnections connections,
        IConnectTicketStore tickets,
        IDocumentRoles roles,
        IngestValidator validator,
        DocumentIngestState state,
        OperationLogBatcher log,
        ILogger<EditorHub> logger)
    {
        ArgumentNullException.ThrowIfNull(catchUp);
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logger);

        _catchUp = catchUp;
        _broadcaster = broadcaster;
        _connections = connections;
        _tickets = tickets;
        _roles = roles;
        _validator = validator;
        _state = state;
        _log = log;
        _logger = logger;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (TryGetBinding(out var binding))
        {
            _connections.Remove(binding.DocumentId, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
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
        _connections.Add(binding.Value.DocumentId, Context.ConnectionId, Context.Abort);

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

        if (!TryGetBinding(out var binding))
        {
            return SubmitResult.Rejected(HubErrors.Unauthenticated);
        }

        // Check one: a field comparison against the binding, no I/O. §7 puts it
        // first because it costs nothing and stops a client submitting into a
        // document it never joined.
        if (batch.DocumentId != binding.DocumentId)
        {
            // not_found, not forbidden: the caller may have no idea whether
            // that document exists, and this answer must not tell them.
            return SubmitResult.Rejected(HubErrors.NotFound);
        }

        // §7's other in-memory check. A client that could name another live
        // replica's id would author operations attributed to it, and every
        // replica would converge on the forgery.
        if (batch.ReplicaId != binding.ReplicaId)
        {
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
            return SubmitResult.Rejected(HubErrors.NotFound);
        }

        if (role == Role.Viewer)
        {
            // §7: rejected and logged. The document id is safe to log — the
            // caller can see the document — and the ticket is not here at all.
            Log.ViewerWriteRejected(_logger, binding.DocumentId, binding.ReplicaId);
            return SubmitResult.Rejected(HubErrors.Forbidden);
        }

        var replicaId = ReplicaIdConversion.FromGuid(binding.ReplicaId);
        var validated = await _validator
            .ValidateAsync(binding.DocumentId, replicaId, batch.Operations, Context.ConnectionAborted)
            .ConfigureAwait(false);

        if (validated.Rejection is not null)
        {
            return SubmitResult.Rejected(validated.Rejection);
        }

        var operations = validated.Operations!;
        if (operations.Count == 0)
        {
            return SubmitResult.Ok(0);
        }

        var appended = await _log.SubmitAsync(binding.DocumentId, operations).ConfigureAwait(false);

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

        return SubmitResult.Ok(operations.Count);
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

        var caught = await _catchUp
            .ReadAsync(binding.DocumentId, vector, forceSnapshot, Context.ConnectionAborted)
            .ConfigureAwait(false);

        return new CatchUpResult(null, caught.Snapshot, caught.Operations, caught.ServerSeq);
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
            EventId = 3401,
            Level = LogLevel.Warning,
            Message = "Viewer write rejected on document {DocumentId} from replica {ReplicaId}.")]
        public static partial void ViewerWriteRejected(ILogger logger, Guid documentId, Guid replicaId);
    }
}
