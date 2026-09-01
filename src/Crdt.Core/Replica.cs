using System.Text;

namespace Crdt.Core;

/// <summary>
/// One FugueMax replica of a text document.
/// </summary>
/// <remarks>
/// <para>
/// Implements the algorithm in PROJECT_SPEC.md §5 — Weidner and Kleppmann's
/// FugueMax, TPDS Algorithm 1 as amended by Definition 6. The document is a
/// tree; the visible text is its depth-first in-order traversal with tombstones
/// skipped.
/// </para>
/// <para>
/// The unit of text is a Unicode code point, never a UTF-16 code unit and never
/// a grapheme cluster (§7). Nothing here normalises: normalisation would change
/// element identity.
/// </para>
/// </remarks>
public sealed class Replica
{
    /// <summary>Creates an empty replica.</summary>
    public Replica(ReplicaId id) => throw new NotImplementedException();

    /// <summary>This replica's identifier.</summary>
    public ReplicaId Id => throw new NotImplementedException();

    /// <summary>The visible text: the traversal with tombstones skipped.</summary>
    public string Text => throw new NotImplementedException();

    /// <summary>The visible text as code points.</summary>
    public IReadOnlyList<Rune> Values => throw new NotImplementedException();

    /// <summary>
    /// Operation counts by replica: the dense high water mark of each replica's
    /// <see cref="ElementId.Seq"/> that this replica has applied.
    /// </summary>
    public IReadOnlyDictionary<ReplicaId, ulong> VersionVector =>
        throw new NotImplementedException();

    /// <summary>
    /// Operations buffered because a dependency has not arrived (§5). A healthy
    /// replica drains this to zero once delivery catches up.
    /// </summary>
    public int PendingCount => throw new NotImplementedException();

    /// <summary>
    /// Element ids of the visible text, positionally aligned with
    /// <see cref="Values"/>.
    /// </summary>
    /// <remarks>
    /// Production API, not a test hook: §9 requires cursors to be anchored to
    /// element ids rather than integer indices, because an index is invalidated
    /// by any concurrent edit earlier in the document.
    /// </remarks>
    public IReadOnlyList<ElementId> VisibleIds => throw new NotImplementedException();

    /// <summary>
    /// Reclaims tombstones that every replica in <paramref name="stableFrontier"/>
    /// has observed, returning how many were collected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Causal stability (§5): an element is collectable only when every
    /// non-retired replica has seen it, so no future legal operation can
    /// reference it. <paramref name="stableFrontier"/> is the elementwise
    /// minimum of the known replicas' version vectors.
    /// </para>
    /// <para>
    /// A tombstone still referenced as a live node's parent or right origin
    /// cannot be removed outright; it keeps its place as a structural
    /// placeholder. Correctness first — a lower reclamation rate is fine, a
    /// broken tree is not.
    /// </para>
    /// </remarks>
    public int Collect(IReadOnlyDictionary<ReplicaId, ulong> stableFrontier) =>
        throw new NotImplementedException();

    /// <summary>
    /// Inserts <paramref name="value"/> at <paramref name="index"/> in the
    /// visible text, applying it locally and returning the operation to broadcast.
    /// </summary>
    public InsertOperation Insert(int index, Rune value) =>
        throw new NotImplementedException();

    /// <summary>
    /// Tombstones the element at <paramref name="index"/> in the visible text,
    /// applying it locally and returning the operation to broadcast.
    /// </summary>
    public DeleteOperation Delete(int index) => throw new NotImplementedException();

    /// <summary>
    /// Applies a remote operation, buffering it if a dependency is missing.
    /// </summary>
    /// <remarks>
    /// Idempotent: applying an operation already seen is a no-op (invariant 2).
    /// An insert depends on its parent and, when it is a right child with a
    /// non-null right origin, on that too — two dependencies, not one.
    /// </remarks>
    public void Apply(Operation operation) => throw new NotImplementedException();

    /// <summary>
    /// Operations this replica knows that a peer at <paramref name="remote"/>
    /// does not, in an order safe to apply.
    /// </summary>
    public IReadOnlyList<Operation> OperationsSince(
        IReadOnlyDictionary<ReplicaId, ulong> remote) =>
        throw new NotImplementedException();
}
