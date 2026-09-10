using System.Security.Claims;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>
/// What a client calls with its OIDC token to get a connect ticket (§7).
/// </summary>
/// <param name="Ticket">Opaque, single-use, valid for at most 60 seconds.</param>
/// <param name="DocumentId">The document the ticket is bound to.</param>
/// <param name="ReplicaId">The replica id the server assigned. The client does not choose it.</param>
/// <param name="Role">The role held at the moment the ticket was issued.</param>
/// <param name="Resumed">
/// Whether <paramref name="ReplicaId"/> is the one the client asked to resume.
/// </param>
/// <remarks>
/// <c>Resumed</c> is <see langword="false"/> whenever the server assigned an id
/// the client did not ask for — including when it asked for none. §7 makes a
/// refused resumption mint a fresh replica rather than fail, so this flag is the
/// client's only signal that its stored replica is gone and its local state must
/// be discarded. Inferring it by comparing ids would work today and would break
/// silently the first time anything else changed the id.
/// </remarks>
public sealed record NegotiateResponse(
    string Ticket, Guid DocumentId, Guid ReplicaId, Role Role, bool Resumed);

/// <summary>What a client may ask for at <c>negotiate</c> (§7).</summary>
/// <param name="ReplicaId">
/// A replica the client believes it owns and wishes to continue, or
/// <see langword="null"/> for a new one.
/// </param>
/// <remarks>
/// A request, never an instruction. §7 verifies ownership before honouring it,
/// and the security property is that resumption authorizes <em>continuing</em> a
/// replica rather than authoring as one — tier-1 still compares each submission
/// against this connection's binding alone.
/// </remarks>
public sealed record NegotiateRequest(Guid? ReplicaId);

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
        NegotiateRequest? request,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        IConnectTicketStore tickets,
        IReplicaClaims claims,
        IUserConnections connections,
        EditorDbContext context,
        DocumentIngestState state,
        IOptions<IngestLimits> limits,
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

        var now = time.GetUtcNow();

        // §7: a client may ask to continue a replica it owns, which is what
        // lets a reload keep the outbox it authored under that id. Verified,
        // never trusted, and a refusal mints a fresh replica rather than
        // failing — a client whose stored replica was retired needs a working
        // session, not a status it cannot act on (§13.13).
        //
        // Decided before the connection cap below, and used by it, because the
        // slot is keyed on the replica and the two paths name different ones.
        var resumable = request?.ReplicaId is { } asked
            && await ResumableAsync(context, documentId, userId.Value, asked, cancellationToken)
                .ConfigureAwait(false);

        // The id this connection will author as, whichever path it takes. The
        // fresh one is minted here rather than further down so that the cap has
        // something to key on: §7 and §13.12 still require the *server* to
        // choose it, and it is generated here for both reasons at once.
        var replicaId = resumable ? request!.ReplicaId!.Value : Guid.CreateVersion7();

        // §7's per-user connection cap, taken here — ABOVE the resumption
        // branch, which returns without ever reaching the replica cap below it.
        //
        // Beside the replica cap is the obvious placement, since that is the
        // other §7 connection-time limit, and it is wrong: a client that asks to
        // resume a replica it owns returns from this method twenty lines from
        // now, so every resumed connection would be uncounted. §13.32 — a check
        // attached to one path does not cover the principals who take another,
        // and a test that only opens fresh sessions would never see it.
        //
        // Resumption costs nothing extra, because the slot is keyed on the
        // replica: a reload re-scores the member it already holds, so a tab that
        // reconnects all afternoon holds one slot rather than one per attempt.
        if (!await connections.TryAdmitAsync(userId.Value, replicaId, cancellationToken)
                .ConfigureAwait(false))
        {
            // 429 rather than 409. The replica cap's 409 says "this document is
            // full"; this says "you are holding too many", which is about the
            // caller rather than the resource and clears on its own as their
            // other tabs close.
            return TypedResults.Json(
                new { code = IngestRejection.TooManyConnections },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (resumable)
        {
            var claimed = replicaId;

            // Taken here rather than at connect: the ticket exists before the
            // connection does, so a claim taken when the socket opens leaves a
            // window in which two negotiate calls both succeed for one replica.
            var held = await claims.TryClaimAsync(documentId, claimed, cancellationToken)
                .ConfigureAwait(false);

            if (held is not null)
            {
                await context.DocumentReplicas
                    .Where(replica => replica.DocumentId == documentId && replica.ReplicaId == claimed)
                    .ExecuteUpdateAsync(
                        update => update.SetProperty(replica => replica.LastSeenAt, now),
                        cancellationToken)
                    .ConfigureAwait(false);

                var resumedTicket = await tickets
                    .IssueAsync(
                        new ConnectionBinding(
                            userId.Value, documentId, claimed, role.Value, held.Value),
                        cancellationToken)
                    .ConfigureAwait(false);

                return TypedResults.Ok(
                    new NegotiateResponse(resumedTicket, documentId, claimed, role.Value, true));
            }

            // Resumable but not claimable: something else holds it, so this
            // connection falls through and becomes a fresh replica. The slot
            // moves with it. Leaving it on the id we are no longer using would
            // hold a place for a replica this connection does not have and
            // release the wrong member on disconnect — a slot leaked per lost
            // race, invisible until it ages out.
            await connections.ReleaseAsync(userId.Value, claimed, cancellationToken)
                .ConfigureAwait(false);

            replicaId = Guid.CreateVersion7();

            if (!await connections.TryAdmitAsync(userId.Value, replicaId, cancellationToken)
                    .ConfigureAwait(false))
            {
                return TypedResults.Json(
                    new { code = IngestRejection.TooManyConnections },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        // §7 caps concurrent replicas per document. Checked here rather than at
        // ingest because this is where a replica comes into existence: refusing
        // the fifty-first at its first keystroke would mean a client that
        // connected, showed the document, and then could not type.
        //
        // After the resumption path, deliberately: resuming creates no replica,
        // so a client reloading into a document already at its cap must not be
        // locked out of work it already owns.
        //
        // Read from Postgres every time. A cached count is a cap that admits an
        // extra replica per cold instance, and this runs once per connection
        // rather than once per operation, so the query is affordable.
        var active = await state.ActiveReplicaCountAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        if (active >= limits.Value.MaxReplicasPerDocument)
        {
            // The slot goes back. Every other refusal above this one happens
            // before it is taken; this is the only path that has one in hand
            // and does not use it, and holding it would charge the caller for a
            // connection the server just refused to give them.
            await connections.ReleaseAsync(userId.Value, replicaId, cancellationToken)
                .ConfigureAwait(false);

            // 409, not 404: the caller is a member and can see the document.
            // Concealing the reason would leave them retrying forever.
            return TypedResults.Conflict(new { code = IngestRejection.TooManyReplicas });
        }

        // §7 and §13.12: the server assigns the replica id — minted above, so
        // that the connection cap had something to key on. A client that chose
        // its own could name another live replica and author operations
        // attributed to it, and every replica would converge on the forgery,
        // because convergence is what the algorithm guarantees.
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

        // A fresh replica nobody can hold yet, so the claim is taken for
        // symmetry rather than for exclusion: the hub renews and releases one
        // claim on every connection without caring how the id was obtained.
        var freshClaim = await claims.TryClaimAsync(documentId, replicaId, cancellationToken)
            .ConfigureAwait(false);

        // Issued last. A ticket handed out before the replica row exists would
        // be redeemable against a binding Postgres has never heard of.
        var ticket = await tickets
            .IssueAsync(
                new ConnectionBinding(
                    userId.Value, documentId, replicaId, role.Value, freshClaim ?? Guid.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(
            new NegotiateResponse(ticket, documentId, replicaId, role.Value, false));
    }

    /// <summary>
    /// §7's checks 1 to 4: the row exists, is this user's, is this document's,
    /// and is not retired.
    /// </summary>
    /// <remarks>
    /// One query rather than four, and it answers only yes or no. Which check
    /// failed is a fact about somebody else's session — "that replica belongs to
    /// another user" is exactly what §7 refuses to tell a caller — and the
    /// caller gets a working replica either way, so the distinction buys them
    /// nothing.
    /// <para>
    /// The retirement check is not redundant with the others (§5): a retired
    /// replica's operations may already have been collected, so continuing to
    /// author under that id would reference elements the GC has forgotten.
    /// </para>
    /// </remarks>
    private static Task<bool> ResumableAsync(
        EditorDbContext context,
        Guid documentId,
        Guid userId,
        Guid replicaId,
        CancellationToken cancellationToken) =>
        context.DocumentReplicas
            .AsNoTracking()
            .AnyAsync(
                replica => replica.DocumentId == documentId
                    && replica.ReplicaId == replicaId
                    && replica.UserId == userId
                    && replica.RetiredAt == null,
                cancellationToken);
}
