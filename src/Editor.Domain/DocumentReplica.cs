namespace Editor.Domain;

/// <summary>A replica that has participated in a document.</summary>
/// <remarks>
/// Backs both the per-document version vector and replica retirement (§5).
/// Causal stability over an open-ended replica set never converges — one browser
/// tab that never returns would block collection forever — so a replica
/// inactive for <c>T_retire</c> (seven days) is retired and must resync from a
/// snapshot if it comes back.
/// </remarks>
public sealed class DocumentReplica
{
    public Guid DocumentId { get; set; }

    public Guid ReplicaId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// Operations applied from this replica, and so the next sequence number
    /// expected from it. Dense from zero (§5).
    /// </summary>
    public long OperationCount { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }
}
