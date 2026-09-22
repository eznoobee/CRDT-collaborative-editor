namespace Editor.Infrastructure.Persistence;

/// <summary>One row of the append-only operation log (PROJECT_SPEC.md §6).</summary>
/// <remarks>
/// <para>
/// The primary key is <c>(DocumentId, ReplicaId, Seq)</c>, which makes duplicate
/// submission a no-op at the database rather than in application code — the
/// cheapest correct place to enforce idempotency.
/// </para>
/// <para>
/// Sequence numbers are <see cref="long"/> here because Postgres has no unsigned
/// <c>bigint</c>, while <c>Crdt.Core</c> uses <see cref="ulong"/>. The mapping is
/// exact below 2^63, which is 9.2 × 10^18 operations from one replica; the
/// conversion is checked rather than truncating, so exceeding it throws instead
/// of silently corrupting an id.
/// </para>
/// </remarks>
public sealed class DocumentOperationRow
{
    public Guid DocumentId { get; set; }

    public Guid ReplicaId { get; set; }

    public long Seq { get; set; }

    /// <summary><c>insert</c> or <c>delete</c>.</summary>
    public required string OpType { get; set; }

    public Guid? ParentReplica { get; set; }

    public long? ParentSeq { get; set; }

    /// <summary><c>L</c> or <c>R</c>; null on a delete.</summary>
    public string? Side { get; set; }

    public Guid? RightOriginReplica { get; set; }

    public long? RightOriginSeq { get; set; }

    /// <summary>
    /// Distinguishes "the right origin is end-of-document" from "this operation
    /// has no right origin". Both leave the id columns null, and conflating them
    /// would lose the difference between a right child at the end of the
    /// document and a left child, which do not order the same way.
    /// </summary>
    public bool RightOriginIsEnd { get; set; }

    /// <summary>Exactly one Unicode code point on an insert; null on a delete.</summary>
    public string? Value { get; set; }

    /// <summary>Delete only: the element being tombstoned.</summary>
    public Guid? TargetReplica { get; set; }

    public long? TargetSeq { get; set; }

    /// <summary>
    /// Per-document delivery order. A reader must never observe a later value
    /// before an earlier one, but gaps are permitted (§6).
    /// </summary>
    public long ServerSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
