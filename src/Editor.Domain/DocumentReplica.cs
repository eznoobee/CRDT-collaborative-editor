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

    /// <summary>
    /// What this replica has acknowledged holding: the largest version vector
    /// below which it has every operation, with no gaps (§5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input to the stability frontier, and the reason the frontier is
    /// computable at all. Everything else in this row describes what the
    /// replica <em>sent</em>; causal stability is a question about what it has
    /// <em>received</em>, and the two are different.
    /// </para><para>
    /// <strong>A prefix, never a maximum.</strong> §8 makes broadcast
    /// unordered, so a replica can hold <c>(s,105)</c> without <c>(s,100)</c>.
    /// Recording the highest thing seen would mark 100 stable while someone is
    /// still missing it, and 100 would then be collected out from under a
    /// client about to name it.
    /// </para><para>
    /// Empty for a replica that has just connected, which is honest: it holds
    /// nothing yet, and the frontier is right to wait for it.
    /// </para>
    /// </remarks>
    public Dictionary<Guid, long> Acknowledged { get; set; } = [];
}
