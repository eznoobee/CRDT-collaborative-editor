using System.Security.Claims;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Documents;

/// <summary>What a caller sends to create a document (§9).</summary>
/// <param name="Title">A human-readable name. Never interpreted, never rendered as markup.</param>
public sealed record CreateDocumentRequest(string? Title);

/// <summary>Who the caller is (§9).</summary>
/// <param name="UserId">The id a grant names.</param>
/// <param name="DisplayName">The name the issuer gave them.</param>
public sealed record Identity(Guid UserId, string DisplayName);

/// <summary>What an owner sends to grant or change a role (§9).</summary>
/// <param name="Role">The role to grant.</param>
public sealed record GrantRoleRequest(Role Role);

/// <summary>A document as the caller can see it (§9).</summary>
/// <param name="Id">The document id, which is also what <c>/d/{id}</c> opens.</param>
/// <param name="Title">The name the owner gave it.</param>
/// <param name="Role">
/// The caller's own role, as the numeric value §6 and the hub already use.
/// </param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
/// <remarks>
/// <c>Role</c> is a number rather than a name because <c>negotiate</c> and the
/// hub protocol already carry it as one and the client reads it as one. A
/// second spelling of the same value in the same product is a conversion nobody
/// remembers to keep in step; readability is not worth that.
/// </remarks>
public sealed record DocumentSummary(
    Guid Id, string Title, Role Role, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The document surface §9 describes.</summary>
/// <remarks>
/// <para>
/// Every handler here decides membership by calling <see cref="IDocumentRoles"/>
/// or <see cref="IDocumentRoleWriter"/>, never by querying
/// <c>document_members</c> itself. A second path to that table would be a second
/// place for §7's five-second revocation bound to be right or wrong, and
/// §13.31 is about how invisible the wrong one is: the bound is still met, by
/// the cache TTL, and every functional test still passes.
/// </para>
/// <para>
/// §7's status rule throughout: a caller with no role on a document gets 404,
/// whether or not it exists, so nothing here reveals that an id is real. A
/// caller who can already see the document and lacks the role for what they
/// asked gets 403, because there is nothing left to conceal.
/// </para>
/// </remarks>
public static class DocumentsEndpoints
{
    /// <summary>The longest title accepted, matching the column (§6).</summary>
    public const int MaximumTitleLength = 512;

    public static IEndpointRouteBuilder MapDocuments(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Who the caller is, in the terms a grant is written in.
        //
        // Without it §9's "grant to a user who has signed in" is not operable:
        // a grant names a user id, an owner has no way to learn anyone else's,
        // and the person being invited has no way to read their own to pass on.
        // That is register row 15's shape exactly — a step whose input nothing
        // in the product produces — and it was found by trying to use the
        // grant path rather than by reading it.
        endpoints.MapGet("/me", WhoAsync).RequireAuthorization();

        // §7's document-API rate limit, on the GROUP rather than on the three
        // endpoints that write (register row 22).
        //
        // Applied here so it cannot be forgotten. A limit added to each write
        // endpoint by hand is the shape 5b.4 already produced once — a control
        // applied to the endpoints someone remembered — and the failure is
        // silent, because an endpoint nobody thought of looks exactly like an
        // endpoint that does not need it. On the group, the next write endpoint
        // added to this file is limited before its author has thought about it.
        //
        // The filter charges by method, so the reads pass through unbilled;
        // that is §7's list and the reasoning is in DocumentApiRateLimitOptions.
        //
        // negotiate is not in this group and is not covered here. It is capped
        // by §7's per-user connection limit instead, which is the right limit
        // for it — the resource it consumes is a connection, not a row.
        var documents = endpoints.MapGroup("/documents").RequireAuthorization();
        documents.AddEndpointFilter(DocumentWriteRateLimitAsync);

        documents.MapPost("/", CreateAsync);
        documents.MapGet("/", ListAsync);
        documents.MapGet("/{documentId:guid}", GetAsync);
        documents.MapGet("/{documentId:guid}/members", ListMembersAsync);
        documents.MapPut("/{documentId:guid}/members/{memberId:guid}", GrantAsync);
        documents.MapDelete("/{documentId:guid}/members/{memberId:guid}", RevokeAsync);

        return endpoints;
    }

    /// <summary>
    /// Charges §7's document-API budget for a request that writes.
    /// </summary>
    /// <remarks>
    /// Before the handler and before authorization has been evaluated against
    /// the target document, deliberately: a loop of calls that will all be
    /// refused with 404 still costs a role lookup and a Postgres round trip
    /// each, so a limit that only counted the calls that got as far as writing
    /// would leave the cheapest attack unbounded. The caller must still be
    /// authenticated — the budget is per user, and there is nobody to charge
    /// otherwise.
    /// </remarks>
    private static async ValueTask<object?> DocumentWriteRateLimitAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var method = context.HttpContext.Request.Method;
        var writes = HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsDelete(method)
            || HttpMethods.IsPatch(method);

        if (!writes)
        {
            return await next(context).ConfigureAwait(false);
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<CurrentUser>();
        var userId = await users
            .ResolveAsync(context.HttpContext.User, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (userId is null)
        {
            // Not this filter's refusal to make. The handler answers an
            // unidentifiable caller with §7's 404, and answering differently
            // here would leak that the route exists.
            return await next(context).ConfigureAwait(false);
        }

        var limits = context.HttpContext.RequestServices
            .GetRequiredService<IDocumentApiRateLimiter>();

        var budget = await limits
            .ChargeWriteAsync(userId.Value, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (budget.Allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        // Retry-After in seconds, per RFC 9110. The hub's throttle carries
        // milliseconds in its own field because it answers a client that
        // resubmits a batch; this answers an HTTP caller, and the header is
        // what an HTTP caller already knows how to read.
        context.HttpContext.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(budget.RetryAfter.TotalSeconds))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

        return TypedResults.Json(
            new { code = IngestRejection.RateLimited },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> CreateAsync(
        CreateDocumentRequest? request,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoleWriter roles,
        EditorDbContext context,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var title = (request?.Title ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > MaximumTitleLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = [$"A title is required and may be at most {MaximumTitleLength} characters."],
            });
        }

        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            // Authenticated, but the token carries no usable identity. There is
            // no user to own the document, and §7 does not distinguish this
            // case for the caller.
            return TypedResults.Unauthorized();
        }

        var now = time.GetUtcNow();
        var document = new Document
        {
            // Version 7 for the same reason as the user key: time-ordered, so
            // inserts do not scatter across the index.
            Id = Guid.CreateVersion7(),
            OwnerId = userId.Value,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Both rows or neither. The owner column alone would keep the document
        // usable without the membership row — DocumentRoleReader treats it as
        // authoritative on purpose — which is exactly why the missing row would
        // go unnoticed: the owner could still edit, and only "the documents I
        // can reach" would quietly omit it.
        await using var transaction = await context.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        context.Documents.Add(document);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await roles.SetRoleAsync(document.Id, userId.Value, Role.Owner, userId.Value, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created(
            $"/documents/{document.Id}",
            new DocumentSummary(document.Id, document.Title, Role.Owner, document.CreatedAt, document.UpdatedAt));
    }

    private static async Task<Results<Ok<IReadOnlyList<DocumentSummary>>, UnauthorizedHttpResult>> ListAsync(
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentMemberships memberships,
        CancellationToken cancellationToken)
    {
        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            return TypedResults.Unauthorized();
        }

        var reachable = await memberships.ListForUserAsync(userId.Value, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DocumentSummary> summaries =
        [
            .. reachable.Select(entry => new DocumentSummary(
                entry.DocumentId, entry.Title, entry.Role, entry.CreatedAt, entry.UpdatedAt)),
        ];

        // An empty list, not a 404. "Nothing yet" is a true and useful answer
        // about the caller's own documents, and it is the state every new user
        // is in.
        return TypedResults.Ok(summaries);
    }

    private static async Task<Results<Ok<DocumentSummary>, NotFound, UnauthorizedHttpResult>> GetAsync(
        Guid documentId,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        EditorDbContext context,
        CancellationToken cancellationToken)
    {
        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            return TypedResults.Unauthorized();
        }

        var role = await roles.GetRoleAsync(documentId, userId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (role is null)
        {
            // The same 404 for "no such document", "deleted", and "not yours".
            return TypedResults.NotFound();
        }

        var found = await context.Documents
            .AsNoTracking()
            .Where(document => document.Id == documentId && document.DeletedAt == null)
            .Select(document => new DocumentSummary(
                document.Id, document.Title, role.Value, document.CreatedAt, document.UpdatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // A role for a document this query cannot see means the document was
        // deleted between the two reads. Same answer as never having had access.
        return found is null ? TypedResults.NotFound() : TypedResults.Ok(found);
    }

    private static async Task<Results<Ok<Identity>, UnauthorizedHttpResult>> WhoAsync(
        ClaimsPrincipal principal,
        CurrentUser users,
        EditorDbContext context,
        CancellationToken cancellationToken)
    {
        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            return TypedResults.Unauthorized();
        }

        var name = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId.Value)
            .Select(user => user.DisplayName)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(new Identity(userId.Value, name ?? string.Empty));
    }

    private static async Task<IResult> ListMembersAsync(
        Guid documentId,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        IDocumentMemberships memberships,
        CancellationToken cancellationToken)
    {
        var caller = await OwnerAsync(documentId, principal, users, roles, cancellationToken)
            .ConfigureAwait(false);

        if (caller.Refusal is { } refusal)
        {
            return refusal;
        }

        var members = await memberships.ListMembersAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok(members);
    }

    private static async Task<IResult> GrantAsync(
        Guid documentId,
        Guid memberId,
        GrantRoleRequest? request,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        IDocumentRoleWriter writer,
        IDocumentMemberships memberships,
        EditorDbContext context,
        CancellationToken cancellationToken)
    {
        if (request is null || !Enum.IsDefined(request.Role))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = ["A role is required, and must be Viewer, Editor or Owner."],
            });
        }

        var caller = await OwnerAsync(documentId, principal, users, roles, cancellationToken)
            .ConfigureAwait(false);

        if (caller.Refusal is { } refusal)
        {
            return refusal;
        }

        if (caller.UserId == memberId)
        {
            // Not merely unwise. DocumentRoleReader answers Owner from the
            // documents.owner_id column whatever document_members says, so a
            // self-demotion would write a row that has no effect — the stored
            // role and the effective one would disagree, and the row is what a
            // reviewer would read.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] = ["An owner cannot change their own role."],
            });
        }

        // §9's stated limitation: a role can be granted only to a user who has
        // signed in. There is no directory and no invitation by email address,
        // and the rejection says so rather than failing at a foreign key
        // (§13.13 — a rejection the rejected party cannot act on is not one).
        var exists = await context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == memberId, cancellationToken)
            .ConfigureAwait(false);

        if (!exists)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] =
                [
                    "No such user. A role can be granted only to someone who has signed in at least once.",
                ],
            });
        }

        await writer.SetRoleAsync(documentId, memberId, request.Role, caller.UserId, cancellationToken)
            .ConfigureAwait(false);

        var members = await memberships.ListMembersAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        var granted = members.FirstOrDefault(member => member.UserId == memberId);
        return granted is null ? TypedResults.NotFound() : TypedResults.Ok(granted);
    }

    private static async Task<IResult> RevokeAsync(
        Guid documentId,
        Guid memberId,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        IDocumentRoleWriter writer,
        EditorDbContext context,
        CancellationToken cancellationToken)
    {
        var caller = await OwnerAsync(documentId, principal, users, roles, cancellationToken)
            .ConfigureAwait(false);

        if (caller.Refusal is { } refusal)
        {
            return refusal;
        }

        // The owner column, not the caller's id: a co-owner granted Owner
        // through document_members must not be able to remove the creator's
        // row either. Left possible, the document would keep the owner it
        // cannot show and lose the one it can.
        var ownerId = await context.Documents
            .AsNoTracking()
            .Where(document => document.Id == documentId)
            .Select(document => document.OwnerId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (memberId == ownerId)
        {
            // A document whose owner has no membership is one nobody can grant
            // on again, and the recovery is someone editing the database.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] = ["A document's owner cannot be removed from it."],
            });
        }

        // Idempotent, and deliberately so: revoking a membership that is
        // already gone is the state the caller wanted, and answering 404 would
        // make a retry after a dropped response look like a different failure.
        await writer.RemoveAsync(documentId, memberId, cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// The caller's id when they own <paramref name="documentId"/>, or the
    /// refusal §7 requires.
    /// </summary>
    /// <remarks>
    /// The 404/403 split lives here once rather than at each call site, because
    /// the two are one decision: 404 while the caller has no role at all, so
    /// nothing reveals that the id is real, and 403 only once they can already
    /// see the document and there is nothing left to conceal.
    /// </remarks>
    private static async Task<(Guid UserId, IResult? Refusal)> OwnerAsync(
        Guid documentId,
        ClaimsPrincipal principal,
        CurrentUser users,
        IDocumentRoles roles,
        CancellationToken cancellationToken)
    {
        var userId = await users.ResolveAsync(principal, cancellationToken).ConfigureAwait(false);
        if (userId is null)
        {
            return (Guid.Empty, TypedResults.Unauthorized());
        }

        var role = await roles.GetRoleAsync(documentId, userId.Value, cancellationToken)
            .ConfigureAwait(false);

        return role switch
        {
            null => (userId.Value, TypedResults.NotFound()),
            Role.Owner => (userId.Value, null),
            _ => (userId.Value, TypedResults.Forbid()),
        };
    }
}
