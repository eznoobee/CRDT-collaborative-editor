namespace Editor.Api.Hubs;

/// <summary>An operation batch as it arrives on the wire.</summary>
/// <param name="DocumentId">The document the client believes it is submitting into.</param>
/// <param name="ReplicaId">The replica the client claims to be.</param>
/// <param name="Operations">The batch, in §6's binary encoding.</param>
/// <remarks>
/// The client sends both ids even though the connection is already bound to
/// them, because §7 requires each to be checked against the binding rather than
/// assumed. A field that is always ignored would be a field nobody notices is
/// wrong.
/// </remarks>
public sealed record OperationBatchMessage(Guid DocumentId, Guid ReplicaId, byte[] Operations);
