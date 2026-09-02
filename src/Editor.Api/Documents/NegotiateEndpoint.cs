using System.Security.Claims;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Tickets;
using Microsoft.AspNetCore.Mvc;

namespace Editor.Api.Documents;

/// <summary>
/// What a client calls with its OIDC token to get a connect ticket (§7).
/// </summary>
/// <param name="Ticket">Opaque, single-use, valid for at most 60 seconds.</param>
/// <param name="DocumentId">The document the ticket is bound to.</param>
/// <param name="ReplicaId">The replica id the server assigned. The client does not choose it.</param>
/// <param name="Role">The role held at the moment the ticket was issued.</param>
public sealed record NegotiateResponse(string Ticket, Guid DocumentId, Guid ReplicaId, Role Role);

/// <summary>The membership decision, and the ticket that carries it.</summary>
public static class NegotiateEndpoint
{
    /// <summary>
    /// Maps <c>POST /documents/{documentId}/negotiate</c>.
    /// </summary>
    /// <remarks>
    /// Not SignalR's own <c>/hub/negotiate</c>. This one runs earlier, with the
    /// OIDC bearer token in a header where it belongs, and produces the ticket
    /// that SignalR's handshake then carries in its URL.
    /// </remarks>
    public static IEndpointRouteBuilder MapNegotiate(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/documents/{documentId:guid}/negotiate", NegotiateAsync)
            .RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> NegotiateAsync(
        Guid documentId,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        IConnectTicketStore tickets,
        EditorDbContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            // A token that authenticated but carries no usable identity is not
            // a member of anything, and §7 says a non-member gets 404.
            return TypedResults.NotFound();
        }

        var role = await roles.GetRoleAsync(documentId, userId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            // 404 rather than 403, and the same 404 for a document that does
            // not exist, one that was deleted, and one this caller is not a
            // member of. §7: do not leak document existence.
            return TypedResults.NotFound();
        }

        // §7 and §13.12: the server assigns the replica id. A client that chose
        // its own could name another live replica and author operations
        // attributed to it — and every replica would converge on the forgery,
        // because convergence is what the algorithm guarantees.
        var replicaId = Guid.CreateVersion7();
        var now = time.GetUtcNow();

        context.DocumentReplicas.Add(new DocumentReplica
        {
            DocumentId = documentId,
            ReplicaId = replicaId,
            UserId = userId.Value,
            LastSeenAt = now,
            OperationCount = 0,
            RetiredAt = null,
        });

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Issued last. A ticket handed out before the replica row exists would
        // be redeemable against a binding Postgres has never heard of.
        var ticket = await tickets
            .IssueAsync(new ConnectionBinding(userId.Value, documentId, replicaId, role.Value), cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new NegotiateResponse(ticket, documentId, replicaId, role.Value));
    }
}
