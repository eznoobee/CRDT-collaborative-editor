using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Authorization;

/// <summary>The authoritative role, read from Postgres.</summary>
/// <remarks>
/// This is the source of truth behind the cache, not something the hot path
/// calls: §8 forbids a database round trip per operation.
/// </remarks>
public sealed class DocumentRoleReader : IDocumentRoles, IDocumentRoleWriter
{
    private readonly EditorDbContext _context;
    private readonly TimeProvider _time;

    public DocumentRoleReader(EditorDbContext context, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);

        _context = context;
        _time = time;
    }

    public async Task<Role?> GetRoleAsync(
        Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        // One query, and it starts from the document: a membership row for a
        // deleted document must not grant anything, and answering from the
        // membership table alone would let a deleted document stay editable.
        var found = await _context.Documents
            .AsNoTracking()
            .Where(document => document.Id == documentId && document.DeletedAt == null)
            .Select(document => new
            {
                document.OwnerId,
                MemberRole = _context.DocumentMembers
                    .Where(member => member.DocumentId == documentId && member.UserId == userId)
                    .Select(member => (Role?)member.Role)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (found is null)
        {
            return null;
        }

        // The owner column is authoritative on its own. A document whose owner
        // row was never mirrored into document_members would otherwise lock its
        // owner out, and the recovery for that is someone editing the database.
        return found.OwnerId == userId ? Role.Owner : found.MemberRole;
    }

    public async Task SetRoleAsync(
        Guid documentId, Guid userId, Role role, Guid grantedBy, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role.");
        }

        var existing = await _context.DocumentMembers
            .FirstOrDefaultAsync(
                member => member.DocumentId == documentId && member.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.DocumentMembers.Add(new DocumentMember
            {
                DocumentId = documentId,
                UserId = userId,
                Role = role,
                GrantedAt = _time.GetUtcNow(),
                GrantedBy = grantedBy,
            });
        }
        else
        {
            existing.Role = role;
            existing.GrantedAt = _time.GetUtcNow();
            existing.GrantedBy = grantedBy;
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        await _context.DocumentMembers
            .Where(member => member.DocumentId == documentId && member.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
