namespace Editor.Api.Hubs;

/// <summary>An operation batch as it arrives on the wire.</summary>
/// <param name="DocumentId">The document the client believes it is submitting into.</param>
/// <param name="ReplicaId">The replica the client claims to be.</param>
/// <param name="Operations">The batch, in §6's binary encoding.</param>
/// <param name="Known">
/// What this replica holds, per replica id, next-expected — §5's acknowledgement
/// piggybacked on a message the client was sending anyway.
/// </param>
/// <remarks>
/// <para>
/// The client sends both ids even though the connection is already bound to
/// them, because §7 requires each to be checked against the binding rather than
/// assumed. A field that is always ignored would be a field nobody notices is
/// wrong.
/// </para><para>
/// <strong><c>Known</c> is register row 32</strong>, the second of §5's three
/// report paths and the one that did not exist. Nullable because it is the
/// third field on an existing message: the interop client does not send it, and
/// a client that omits it must submit normally rather than fail. Absent and
/// empty are the same thing here — a vector reporting nothing is not a report.
/// </para><para>
/// <strong>It describes what the replica holds including the batch it is
/// sending</strong>, which the server has not written yet. That is safe only
/// because §5's frontier clamps every author's acknowledged position to what
/// the log actually contains: without that clamp, a client's claim about its
/// own unsent operations would advance the frontier past operations the server
/// could still be asked to replay.
/// </para>
/// </remarks>
public sealed record OperationBatchMessage(
    Guid DocumentId, Guid ReplicaId, byte[] Operations, Dictionary<Guid, long>? Known = null);
