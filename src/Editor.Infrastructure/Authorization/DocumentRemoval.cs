using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Authorization;

/// <summary>Removing a document, and the invalidation it must trigger.</summary>
public interface IDocumentRemoval
{
    /// <summary>
    /// Soft-deletes a document; answers whether one was there to remove.
    /// </summary>
    Task<bool> RemoveAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// §9's document removal: `deleted_at`, and every cached role that outlived it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Writing <c>deleted_at</c> is the half that already worked.</strong>
/// Every path that reads the document row has filtered on it since Phase 2, so
/// a handler that only wrote the column would make the listings, the metadata
/// read and a fresh <c>negotiate</c> all behave correctly — and would leave the
/// person who already had the document open typing into it, indefinitely.
/// Their submissions are checked against <see cref="CachedDocumentRoles"/>, and
/// so is the sweep that would otherwise close them, so nothing in the live path
/// reads the row this method writes.
/// </para><para>
/// §13.32 in its most literal form: the enforcement is keyed on reading, and
/// the user who never reads is the one already connected. The fix is the one §7
/// already uses for revocation — invalidate — and the only difference is that a
/// deletion has no single user, so it invalidates for every member.
/// </para><para>
/// <strong>And "the half that already worked" was smaller than it looked.</strong>
/// Removing the invalidation turns three tests red, not two: the listing and the
/// metadata read do query the document row and go on behaving, but
/// <c>negotiate</c> asks <see cref="IDocumentRoles"/> — so on a stale cache a
/// client could still open a brand-new connection to a document that had been
/// removed. The cache is not a performance detail sitting beside the deletion
/// check; for everything on the live path it <em>is</em> the deletion check.
/// </para><para>
/// One class rather than the decorator <see cref="InvalidatingDocumentRoleWriter"/>
/// uses, for the same reason that decorator exists: there must be no way to
/// perform this write without the invalidation. There was no pre-existing
/// remover to wrap, so the way to leave no other path is for the only public
/// method to do both.
/// </para>
/// </remarks>
public sealed class DocumentRemoval : IDocumentRemoval
{
    private readonly EditorDbContext _context;
    private readonly IDocumentMemberships _memberships;
    private readonly CachedDocumentRoles _cache;
    private readonly TimeProvider _time;

    public DocumentRemoval(
        EditorDbContext context,
        IDocumentMemberships memberships,
        CachedDocumentRoles cache,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);

        _context = context;
        _memberships = memberships;
        _cache = cache;
        _time = time;
    }

    public async Task<bool> RemoveAsync(Guid documentId, CancellationToken cancellationToken)
    {
        // Read before the write. Once deleted_at is set the listing excludes
        // the document, and with it the members whose cached roles are the
        // whole point of this method.
        var members = await _memberships.ListMembersAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        var document = await _context.Documents
            .SingleOrDefaultAsync(
                row => row.Id == documentId && row.DeletedAt == null, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return false;
        }

        document.DeletedAt = _time.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // After the commit, never before. An invalidation that ran first would
        // leave a window in which an instance re-reads the role, finds the
        // document still live, and caches it again for the full TTL — which is
        // the failure this method exists to prevent, arriving by way of the fix.
        foreach (var member in members)
        {
            await _cache.InvalidateAsync(documentId, member.UserId, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }
}
