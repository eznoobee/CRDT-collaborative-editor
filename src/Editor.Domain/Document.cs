namespace Editor.Domain;

/// <summary>A collaboratively edited document.</summary>
public sealed class Document
{
    public Guid Id { get; set; }

    public Guid OwnerId { get; set; }

    public required string Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft deletion. The operation log is retained.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// §5's GC watermark: the causal-stability frontier below which elements
    /// may have been collected.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed on demand, for two reasons that pull the
    /// same way. It must never move backwards, and the only way to enforce that
    /// is to compare against what it was; and an operation naming an id at or
    /// below it is answered with <c>resync_required</c> (§9), which is a
    /// request-path decision that cannot afford to recompute a minimum over
    /// every replica.
    /// </remarks>
    public Dictionary<Guid, long> StabilityFrontier { get; set; } = [];

    /// <summary>When a collection sweep last examined this document.</summary>
    /// <remarks>
    /// Stored so a sweep that examines a bounded batch rotates through every
    /// document rather than through the first <c>BatchSize</c> of them. Without
    /// it the batch is stable under any fixed ordering, and the documents past
    /// the end are never collected at all — the same shape as §13.32, with
    /// "the document that sorts late" in place of a user who never acts.
    /// Written on every sweep, whether or not anything was collected, because
    /// it records that the document was looked at.
    /// </remarks>
    public DateTimeOffset? LastCollectedAt { get; set; }
}
