using Editor.Domain;
using Editor.Infrastructure.Authorization;
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

    private readonly IConnectTicketStore _tickets;
    private readonly IDocumentRoles _roles;
    private readonly ILogger<EditorHub> _logger;

    public EditorHub(IConnectTicketStore tickets, IDocumentRoles roles, ILogger<EditorHub> logger)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(logger);

        _tickets = tickets;
        _roles = roles;
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
    /// This is §7's two checks and nothing else yet: the operations themselves
    /// are validated in the next task, and until then a batch that passes
    /// authorization is accepted and not applied. Broadcasting or persisting it
    /// before validation exists would be the stub §12 forbids.
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

        return SubmitResult.Ok(batch.Operations.LongLength);
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
