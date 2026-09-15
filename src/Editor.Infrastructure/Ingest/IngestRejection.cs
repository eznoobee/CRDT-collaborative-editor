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
}
