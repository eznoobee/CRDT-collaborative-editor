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

/// <summary>Membership changes, and the invalidation they must trigger.</summary>
public interface IDocumentRoleWriter
{
    /// <summary>Grants or changes a role, and invalidates the cached one.</summary>
    Task SetRoleAsync(
        Guid documentId, Guid userId, Role role, Guid grantedBy, CancellationToken cancellationToken);

    /// <summary>Removes a membership, and invalidates the cached role.</summary>
    Task RemoveAsync(Guid documentId, Guid userId, CancellationToken cancellationToken);
}
