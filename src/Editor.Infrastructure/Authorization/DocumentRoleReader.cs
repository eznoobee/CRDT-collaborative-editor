using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Authorization;

/// <summary>The authoritative role, read from Postgres.</summary>
/// <remarks>
/// This is the source of truth behind the cache, not something the hot path
/// calls: §8 forbids a database round trip per operation.
/// </remarks>
public sealed class DocumentRoleReader : IDocumentRoles, IDocumentRoleWriter, IDocumentMemberships
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

    public async Task<IReadOnlyList<DocumentMembership>> ListForUserAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        // Owner column OR membership row, matching GetRoleAsync exactly. A
        // listing built from document_members alone would omit any document
        // whose owner row was never mirrored there — which GetRoleAsync
        // tolerates on purpose, so the two would disagree about the same
        // document and only the listing would be wrong.
        return await _context.Documents
            .AsNoTracking()
            .Where(document => document.DeletedAt == null)
            .Select(document => new
            {
                document.Id,
                document.Title,
                document.CreatedAt,
                document.UpdatedAt,
                document.OwnerId,
                MemberRole = _context.DocumentMembers
                    .Where(member => member.DocumentId == document.Id && member.UserId == userId)
                    .Select(member => (Role?)member.Role)
                    .FirstOrDefault(),
            })
            .Where(row => row.OwnerId == userId || row.MemberRole != null)
            .OrderByDescending(row => row.UpdatedAt)
            .Select(row => new DocumentMembership(
                row.Id,
                row.Title,
                row.OwnerId == userId ? Role.Owner : row.MemberRole!.Value,
                row.CreatedAt,
                row.UpdatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentMemberEntry>> ListMembersAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        // The owner appears here because 6.1 writes their membership row, not
        // because this query synthesises one. A document created before that
        // path existed shows no owner, and that is the truth about its rows
        // rather than something to paper over in a projection.
        return await _context.DocumentMembers
            .AsNoTracking()
            .Where(member => member.DocumentId == documentId)
            .Join(
                _context.Users.AsNoTracking(),
                member => member.UserId,
                user => user.Id,
                (member, user) => new DocumentMemberEntry(
                    member.UserId, user.DisplayName, member.Role, member.GrantedAt))
            .OrderBy(entry => entry.GrantedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
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
