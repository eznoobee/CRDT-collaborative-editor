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

    private readonly IConnectTicketStore _tickets;
    private readonly IDocumentRoles _roles;
    private readonly IngestValidator _validator;
    private readonly DocumentIngestState _state;
    private readonly OperationLogBatcher _log;
    private readonly ILogger<EditorHub> _logger;

    public EditorHub(
        IConnectTicketStore tickets,
        IDocumentRoles roles,
        IngestValidator validator,
        DocumentIngestState state,
        OperationLogBatcher log,
        ILogger<EditorHub> logger)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logger);

        _tickets = tickets;
        _roles = roles;
        _validator = validator;
        _state = state;
        _log = log;
        _logger = logger;
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
        // The sender is excluded because it already has these operations. That
        // is an optimisation and not a correctness requirement — §5 makes
        // re-delivery harmless — but the exclusion is asserted, because a hub
        // echoing every batch to its author doubles fan-out for nothing.
        await Clients.GroupExcept(Group(binding.DocumentId), Context.ConnectionId)
            .SendAsync(
                Broadcast,
                new OperationBroadcast(binding.DocumentId, batch.Operations, appended.HighestServerSeq),
                Context.ConnectionAborted)
            .ConfigureAwait(false);

        return SubmitResult.Ok(operations.Count);
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
            EventId = 3401,
            Level = LogLevel.Warning,
            Message = "Viewer write rejected on document {DocumentId} from replica {ReplicaId}.")]
        public static partial void ViewerWriteRejected(ILogger logger, Guid documentId, Guid replicaId);
    }
}
