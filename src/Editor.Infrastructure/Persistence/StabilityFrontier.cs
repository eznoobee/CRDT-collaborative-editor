using Microsoft.EntityFrameworkCore;

namespace Editor.Infrastructure.Persistence;

/// <summary>What a frontier computation found.</summary>
/// <param name="Frontier">
/// Per replica, the highest sequence number every live replica is known to
/// hold. An operation <c>(s, n)</c> is causally stable when <c>n &lt;=
/// Frontier[s]</c>.
/// </param>
/// <param name="Advanced">
/// Whether this computation moved the stored frontier forward.
/// </param>
/// <param name="Live">How many non-retired replicas the minimum was taken over.</param>
public readonly record struct FrontierResult(
    IReadOnlyDictionary<Guid, long> Frontier, bool Advanced, int Live);

/// <summary>§5's causal-stability frontier.</summary>
public interface IStabilityFrontier
{
    /// <summary>Records what a replica holds.</summary>
    Task AcknowledgeAsync(
        Guid documentId,
        Guid replicaId,
        IReadOnlyDictionary<Guid, long> acknowledged,
        CancellationToken cancellationToken);

    /// <summary>Recomputes the document's frontier and stores it if it moved.</summary>
    Task<FrontierResult> AdvanceAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>The stored frontier, without recomputing.</summary>
    Task<IReadOnlyDictionary<Guid, long>> CurrentAsync(
        Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// The frontier over what replicas have acknowledged (PROJECT_SPEC.md §5).
/// </summary>
/// <remarks>
/// <para>
/// The pointwise minimum of every non-retired replica's acknowledged version
/// vector. Retirement is what lets it move at all: one browser tab that never
/// returns would otherwise hold it still forever, which is why register row 1
/// blocks this work rather than merely preceding it.
/// </para><para>
/// <strong>It never moves backwards.</strong> The frontier is recomputed rather
/// than accumulated, and a recomputation that produced a lower value would mean
/// a replica un-observed something — impossible if the inputs are prefixes, and
/// a bug if it happens. Storing the maximum of old and new turns that bug into
/// a frontier that stalls, which is safe, instead of one that retreats after GC
/// has already collected below it, which is not.
/// </para>
/// </remarks>
public sealed class StabilityFrontier : IStabilityFrontier
{
    private readonly EditorDbContext _context;

    public StabilityFrontier(EditorDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task AcknowledgeAsync(
        Guid documentId,
        Guid replicaId,
        IReadOnlyDictionary<Guid, long> acknowledged,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);

        var replica = await _context.DocumentReplicas
            .SingleOrDefaultAsync(
                row => row.DocumentId == documentId && row.ReplicaId == replicaId,
                cancellationToken)
            .ConfigureAwait(false);

        if (replica is null)
        {
            return;
        }

        // Pointwise maximum against what is already recorded, never a
        // replacement. A client reports a prefix it holds; a later report that
        // named fewer replicas would otherwise erase entries and drag the
        // frontier back, and a client is entitled to send a vector covering
        // only what it has heard of.
        foreach (var (author, seq) in acknowledged)
        {
            if (!replica.Acknowledged.TryGetValue(author, out var held) || seq > held)
            {
                replica.Acknowledged[author] = seq;
            }
        }

        // The dictionary is mutated in place, which EF's change tracker cannot
        // see through a jsonb value converter comparing by reference.
        _context.Entry(replica).Property(row => row.Acknowledged).IsModified = true;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FrontierResult> AdvanceAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var live = await _context.DocumentReplicas
            .Where(row => row.DocumentId == documentId && row.RetiredAt == null)
            .Select(row => row.Acknowledged)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var document = await _context.Documents
            .SingleOrDefaultAsync(row => row.Id == documentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return new FrontierResult(new Dictionary<Guid, long>(), false, 0);
        }

        var computed = live.Count == 0
            ? await EverythingAsync(documentId, cancellationToken).ConfigureAwait(false)
            : Minimum(live, await AuthorsAsync(documentId, cancellationToken).ConfigureAwait(false));

        var advanced = false;
        foreach (var (author, seq) in computed)
        {
            if (!document.StabilityFrontier.TryGetValue(author, out var stored) || seq > stored)
            {
                document.StabilityFrontier[author] = seq;
                advanced = true;
            }
        }

        if (advanced)
        {
            _context.Entry(document).Property(row => row.StabilityFrontier).IsModified = true;
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return new FrontierResult(document.StabilityFrontier, advanced, live.Count);
    }

    public async Task<IReadOnlyDictionary<Guid, long>> CurrentAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var stored = await _context.Documents
            .Where(row => row.Id == documentId)
            .Select(row => row.StabilityFrontier)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored ?? new Dictionary<Guid, long>();
    }

    /// <summary>
    /// The frontier for a document with no live replicas: everything.
    /// </summary>
    /// <remarks>
    /// <strong>The empty set is not zero, and getting this backwards is the
    /// mistake that leaves abandoned documents uncollectable forever.</strong>
    /// A minimum over nothing has no natural value, and every obvious
    /// implementation answers "nothing is stable" — which is the exact opposite
    /// of the truth. Causal stability asks whether any live replica might still
    /// be missing an operation; when there are no live replicas, none can be,
    /// so every operation the document contains is stable.
    /// <para>
    /// The failure is invisible by construction: a document nobody has open is
    /// a document nobody is looking at, and it is also the one whose tombstones
    /// are most worth reclaiming.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<Guid, long>> EverythingAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var rows = await _context.DocumentOperations
            .Where(row => row.DocumentId == documentId)
            .GroupBy(row => row.ReplicaId)
            .Select(group => new { Replica = group.Key, Highest = group.Max(row => row.Seq) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(row => row.Replica, row => row.Highest);
    }

    /// <summary>Every replica that has ever written to the document.</summary>
    private async Task<List<Guid>> AuthorsAsync(
        Guid documentId, CancellationToken cancellationToken) =>
        await _context.DocumentOperations
            .Where(row => row.DocumentId == documentId)
            .Select(row => row.ReplicaId)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The pointwise minimum, over the authors that exist.</summary>
    /// <remarks>
    /// Taken over the authors rather than over the keys present in the
    /// acknowledgements, because a replica that has never heard of an author
    /// holds nothing from it — its contribution to that author's minimum is
    /// zero, and skipping the missing key would read as "no constraint" and
    /// mark that author's whole history stable.
    /// </remarks>
    private static Dictionary<Guid, long> Minimum(
        List<Dictionary<Guid, long>> acknowledged, List<Guid> authors)
    {
        var frontier = new Dictionary<Guid, long>(authors.Count);

        foreach (var author in authors)
        {
            var lowest = long.MaxValue;
            foreach (var replica in acknowledged)
            {
                var held = replica.TryGetValue(author, out var seq) ? seq : 0;
                if (held < lowest)
                {
                    lowest = held;
                }
            }

            frontier[author] = lowest;
        }

        return frontier;
    }
}
