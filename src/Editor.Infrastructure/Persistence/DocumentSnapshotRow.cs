namespace Editor.Infrastructure.Persistence;

/// <summary>A periodic snapshot of a document's state (PROJECT_SPEC.md §6).</summary>
/// <remarks>
/// <see cref="State"/> holds the <b>binary</b> encoding of §6, not the normative
/// JSON. The two are tied together by §9's requirement that
/// <c>binary → JSON → binary</c> round-trips byte-identically on both
/// implementations, which the conformance corpus checks on every trace — so an
/// encoding divergence between the server and the client is still a failing
/// build, without a snapshot costing 222.8 bytes an element to say what 16 says
/// (§13.9).
/// </remarks>
public sealed class DocumentSnapshotRow
{
    public Guid DocumentId { get; set; }

    /// <summary>The document is this snapshot plus every operation after here.</summary>
    public long ServerSeq { get; set; }

    public required byte[] State { get; set; }

    /// <summary>
    /// Replica id to operation count, as decimal strings (§6): JSON numbers are
    /// doubles and stop round-tripping above 2^53.
    /// </summary>
    public required string VersionVector { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
