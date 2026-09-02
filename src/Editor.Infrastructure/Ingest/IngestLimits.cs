using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Ingest;

/// <summary>
/// PROJECT_SPEC.md §7's caps. All configurable, all enforced server-side.
/// </summary>
/// <remarks>
/// Configurable does not mean unbounded: each property has a range, so a
/// deployment can tighten a cap and cannot lift it past what the rest of the
/// system is built for. A 5 GB document limit set by configuration would not be
/// a policy choice, it would be an outage.
/// </remarks>
public sealed class IngestLimits
{
    public const string Section = "Ingest";

    /// <summary>Bytes in one hub message.</summary>
    [Range(1024, 64 * 1024)]
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    /// <summary>Operations in one batch.</summary>
    [Range(1, 256)]
    public int MaxOperationsPerBatch { get; set; } = 256;

    /// <summary>Code points in one run operation.</summary>
    /// <remarks>
    /// Enforced by the ingest path from Phase 3 on. Runs are expanded into
    /// individual elements on arrival, so this bounds the expansion as well as
    /// the message.
    /// </remarks>
    [Range(1, 256)]
    public int MaxRunCodePoints { get; set; } = 256;

    /// <summary>Bytes of live (non-tombstoned) text in one document.</summary>
    [Range(1024, 5 * 1024 * 1024)]
    public int MaxDocumentBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Replicas that may be active on one document at once.</summary>
    [Range(1, 50)]
    public int MaxReplicasPerDocument { get; set; } = 50;
}
