using Editor.Domain;

namespace Editor.Infrastructure.Authorization;

/// <summary>
/// The caller's role on a document, for §7's second authorization check.
/// </summary>
public interface IDocumentRoles
{
    /// <summary>
    /// The role <paramref name="userId"/> holds on <paramref name="documentId"/>.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the caller has no role, which covers three
    /// cases §7 requires be indistinguishable: no such document, a deleted
    /// document, and a document the caller is not a member of. Telling them
    /// apart is how document existence leaks.
    /// </returns>
    Task<Role?> GetRoleAsync(Guid documentId, Guid userId, CancellationToken cancellationToken);
}

/// <summary>One document a user can reach, and the role they hold on it.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Title">Its title.</param>
/// <param name="Role">The role this user holds, with the owner column winning.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="UpdatedAt">When it last changed.</param>
public sealed record DocumentMembership(
    Guid DocumentId, string Title, Role Role, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Enumerating what a user can reach (§9).</summary>
/// <remarks>
/// <para>
/// Deliberately separate from <see cref="IDocumentRoles"/> and deliberately
/// **uncached**, and the distinction is worth stating because §9 requires the
/// membership decision to go through one path.
/// </para><para>
/// A listing is not an authorization decision. Nothing is permitted on the
/// strength of appearing in it: opening a document still calls
/// <c>negotiate</c>, which re-reads the role, and every operation is re-checked
/// after that. What a listing is, is an enumeration — and the failure mode of a
/// stale one runs the wrong way, since a user who was revoked five seconds ago
/// should not still be shown the document as reachable. So it reads Postgres
/// every time and carries no TTL to be inside of.
/// </para><para>
/// It lives on the same implementation as the role read rather than in an
/// endpoint, so that <c>document_members</c> still has exactly one class that
/// queries it and the owner-column rule is written once.
/// </para>
/// </remarks>
public interface IDocumentMemberships
{
    /// <summary>
    /// Every document <paramref name="userId"/> can reach, most recently
    /// updated first. Soft-deleted documents are excluded.
    /// </summary>
    Task<IReadOnlyList<DocumentMembership>> ListForUserAsync(
        Guid userId, CancellationToken cancellationToken);

    /// <summary>Who holds a role on <paramref name="documentId"/>.</summary>
    Task<IReadOnlyList<DocumentMemberEntry>> ListMembersAsync(
        Guid documentId, CancellationToken cancellationToken);
}

/// <summary>One member of a document (§9).</summary>
/// <param name="UserId">The member's id, which is what a grant names.</param>
/// <param name="DisplayName">Their display name, as the issuer gave it.</param>
/// <param name="Role">The role they hold.</param>
/// <param name="GrantedAt">When it was granted.</param>
public sealed record DocumentMemberEntry(
    Guid UserId, string DisplayName, Role Role, DateTimeOffset GrantedAt);

/// <summary>Membership changes, and the invalidation they must trigger.</summary>
public interface IDocumentRoleWriter
{
    /// <summary>Grants or changes a role, and invalidates the cached one.</summary>
    Task SetRoleAsync(
        Guid documentId, Guid userId, Role role, Guid grantedBy, CancellationToken cancellationToken);

    /// <summary>Removes a membership, and invalidates the cached role.</summary>
    Task RemoveAsync(Guid documentId, Guid userId, CancellationToken cancellationToken);
}
