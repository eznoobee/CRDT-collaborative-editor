namespace Editor.Infrastructure.Ingest;

/// <summary>Why a batch was refused (PROJECT_SPEC.md §7).</summary>
/// <remarks>
/// One code per rule, and each reaches the client as the whole of the answer.
/// They are distinguishable from each other on purpose — unlike §7's
/// 404-versus-403 rule, there is nothing to conceal here: the caller is a member
/// of the document and every one of these describes something about the message
/// they just sent, which they already know.
/// </remarks>
public static class IngestRejection
{
    /// <summary>The message exceeded the byte cap.</summary>
    public const string MessageTooLarge = "message_too_large";

    /// <summary>The batch held more operations than the cap allows.</summary>
    public const string BatchTooLarge = "batch_too_large";

    /// <summary>A run named more code points than the cap allows.</summary>
    public const string RunTooLong = "run_too_long";

    /// <summary>The bytes were not a well-formed operation batch.</summary>
    public const string Malformed = "malformed";

    /// <summary>An operation claimed a replica other than the connection's.</summary>
    public const string ReplicaMismatch = "replica_mismatch";

    /// <summary>A sequence number was not the next dense value for that replica.</summary>
    public const string SequenceGap = "sequence_gap";

    /// <summary>
    /// An operation referenced a parent, right origin or delete target the
    /// document does not contain.
    /// </summary>
    public const string UnknownOrigin = "unknown_origin";

    /// <summary>The document is at its size cap.</summary>
    public const string DocumentFull = "document_full";

    /// <summary>The document already has as many replicas as it may have.</summary>
    public const string TooManyReplicas = "too_many_replicas";

    /// <summary>
    /// §7's abuse limits. The caller is over budget and should wait.
    /// </summary>
    /// <remarks>
    /// A structured refusal rather than a silent drop, which §7 requires by
    /// name: a client whose operations vanish renders a document that is wrong
    /// without knowing, and retries forever because nothing told it not to
    /// (§13.13).
    /// </remarks>
    public const string RateLimited = "rate_limited";

    /// <summary>
    /// §7's per-user connection cap. This caller holds too many connections.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TooManyReplicas"/> on purpose, and the two are
    /// easy to conflate because both refuse a connection. That one is about the
    /// document — it is full, and nothing this caller closes will change it.
    /// This one is about the caller, and clears as their own tabs close. A
    /// client cannot say anything useful to the person in front of it without
    /// knowing which.
    /// </remarks>
    public const string TooManyConnections = "too_many_connections";
}
