using System.Security.Claims;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Documents;

/// <summary>What a caller sends to create a document (§9).</summary>
/// <param name="Title">A human-readable name. Never interpreted, never rendered as markup.</param>
public sealed record CreateDocumentRequest(string? Title);

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

        var documents = endpoints.MapGroup("/documents").RequireAuthorization();

        documents.MapPost("/", CreateAsync);
        documents.MapGet("/", ListAsync);
        documents.MapGet("/{documentId:guid}", GetAsync);

        return endpoints;
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
}
