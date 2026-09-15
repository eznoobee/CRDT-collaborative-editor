namespace Editor.Infrastructure.Persistence;

/// <summary>A periodic snapshot of a document's state (PROJECT_SPEC.md §6).</summary>
/// <remarks>
/// <see cref="State"/> holds the normalised JSON of §9 — the same format the
/// conformance runners emit — so a snapshot is directly comparable against a
/// conformance artefact and an encoding divergence between the server and the
/// client shows up as a failing build.
/// </remarks>
public sealed class DocumentSnapshotRow
{
    public Guid DocumentId { get; set; }

    /// <summary>The document is this snapshot plus every operation after here.</summary>
    public long ServerSeq { get; set; }

    public required string State { get; set; }

    /// <summary>
    /// Replica id to operation count, as decimal strings (§6): JSON numbers are
    /// doubles and stop round-tripping above 2^53.
    /// </summary>
    public required string VersionVector { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
