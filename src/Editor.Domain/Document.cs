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
}
