using Crdt.Core;
using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Persistence;

/// <summary>§5's tombstone collection, applied to stored documents.</summary>
public interface ISnapshotGarbageCollector
{
    /// <summary>
    /// Collects across up to <paramref name="documents"/> documents; answers how
    /// many elements were removed.
    /// </summary>
    Task<int> CollectAsync(int documents, CancellationToken cancellationToken);

    /// <summary>Collects one document; answers how many elements were removed.</summary>
    Task<int> CollectDocumentAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// Collection against the snapshot, never against the operation log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The log is append-only and stays that way.</strong> Collecting by
/// deleting rows from <c>document_ops</c> would change what a full replay
/// produces, which is the one reconstruction this system can always fall back
/// on — and it would do so irreversibly. The snapshot is a cache of the replay
/// (§8), so shrinking it costs nothing that cannot be rebuilt, right up until
/// the log is truncated behind it, which is a separate decision this does not
/// make.
/// </para><para>
/// That means collection here is <em>not yet</em> a reclamation of storage. It
/// is the correctness half: proving that a collected replica behaves
/// identically to one that never collected. Truncating the log behind a
/// collected snapshot is what turns it into reclaimed bytes, and it belongs
/// with the load measurements in 7b where the benefit can be measured rather
/// than assumed.
/// </para>
/// </remarks>
public sealed class SnapshotGarbageCollector : ISnapshotGarbageCollector
{
    private readonly EditorDbContext _context;
    private readonly DocumentStore _store;
    private readonly IStabilityFrontier _frontier;
    private readonly TimeProvider _time;

    public SnapshotGarbageCollector(
        EditorDbContext context,
        DocumentStore store,
        IStabilityFrontier frontier,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(frontier);
        ArgumentNullException.ThrowIfNull(time);

        _context = context;
        _store = store;
        _frontier = frontier;
        _time = time;
    }

    public async Task<int> CollectAsync(int documents, CancellationToken cancellationToken)
    {
        // Least recently examined first, nulls before them. A fixed ordering
        // would make the batch the same documents every sweep and the rest
        // uncollectable forever; see Document.LastCollectedAt.
        var candidates = await _context.Documents
            .OrderBy(row => row.LastCollectedAt == null ? 0 : 1)
            .ThenBy(row => row.LastCollectedAt)
            .Select(row => row.Id)
            .Take(documents)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var collected = 0;
        foreach (var documentId in candidates)
        {
            collected += await CollectDocumentAsync(documentId, cancellationToken)
                .ConfigureAwait(false);
        }

        return collected;
    }

    public async Task<int> CollectDocumentAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _context.Documents
            .SingleOrDefaultAsync(row => row.Id == documentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return 0;
        }

        // Marked as examined before anything else, and saved even when nothing
        // is collected. The stamp records that the sweep looked, which is what
        // rotates the batch; making it conditional on a non-zero count would
        // park the batch on the documents with nothing to collect.
        document.LastCollectedAt = _time.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Recomputed rather than read, so a sweep never collects against a
        // frontier that a retirement since made stale in the unsafe direction.
        await _frontier.AdvanceAsync(documentId, cancellationToken).ConfigureAwait(false);
        var frontier = await _frontier.CurrentAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        if (frontier.Count == 0)
        {
            return 0;
        }

        // Loaded as the document's own id, which is what the catch-up path
        // uses. The identity matters only for operations this replica would
        // author, and collection authors none.
        var (replica, serverSeq) = await _store
            .LoadForCollectionAsync(
                documentId, ReplicaIdConversion.FromGuid(documentId), cancellationToken)
            .ConfigureAwait(false);

        // Next-expected form on both sides, so this is a rename and not a
        // conversion. §5 is explicit that there must be no arithmetic here.
        var watermark = new Dictionary<ReplicaId, ulong>(frontier.Count);
        foreach (var (author, count) in frontier)
        {
            watermark[ReplicaIdConversion.FromGuid(author)] = ReplicaIdConversion.ToUInt64(count);
        }

        var collected = replica.Collect(watermark);
        if (collected == 0)
        {
            return 0;
        }

        await _store
            .SaveCollectedSnapshotAsync(documentId, replica, serverSeq, cancellationToken)
            .ConfigureAwait(false);

        return collected;
    }
}
