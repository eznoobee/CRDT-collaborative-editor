using Editor.Domain;

namespace Editor.Infrastructure.Authorization;

/// <summary>
/// A membership change that cannot be made without invalidating the cache.
/// </summary>
/// <remarks>
/// The invalidation is a decorator rather than a call sites' responsibility on
/// purpose: §7 requires revocation to take effect within five seconds, and a
/// membership change that forgot to invalidate would be correct in Postgres and
/// wrong everywhere it matters for as long as the TTL allows. The way to keep
/// that from happening is to leave no way to write without it.
/// </remarks>
public sealed class InvalidatingDocumentRoleWriter : IDocumentRoleWriter
{
    private readonly IDocumentRoleWriter _inner;
    private readonly CachedDocumentRoles _cache;

    public InvalidatingDocumentRoleWriter(IDocumentRoleWriter inner, CachedDocumentRoles cache)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);

        _inner = inner;
        _cache = cache;
    }

    public async Task SetRoleAsync(
        Guid documentId, Guid userId, Role role, Guid grantedBy, CancellationToken cancellationToken)
    {
        await _inner.SetRoleAsync(documentId, userId, role, grantedBy, cancellationToken)
            .ConfigureAwait(false);
        await _cache.InvalidateAsync(documentId, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        await _inner.RemoveAsync(documentId, userId, cancellationToken).ConfigureAwait(false);
        await _cache.InvalidateAsync(documentId, userId, cancellationToken).ConfigureAwait(false);
    }
}
