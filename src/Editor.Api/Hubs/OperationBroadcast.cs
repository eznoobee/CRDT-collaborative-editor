namespace Editor.Api.Hubs;

/// <summary>A batch fanned out to the other connections on a document.</summary>
/// <param name="DocumentId">The document these operations belong to.</param>
/// <param name="Operations">The batch, in §6's binary encoding, as opaque bytes.</param>
/// <param name="ServerSeq">
/// The highest <c>server_seq</c> assigned to this batch.
/// </param>
/// <remarks>
/// <para>
/// <c>Operations</c> is a byte string and stays one. §6 is the sole
/// authoritative encoding, and the transport frames it without knowing what is
/// inside — passing structured operations to MessagePack's serialiser would
/// create a second encoding with its own canonical form, which is where
/// §13.11's bug came from.
/// </para>
/// <para>
/// <c>ServerSeq</c> is carried for diagnostics and gap detection, and is
/// deliberately <b>not</b> what a client uses as a catch-up cursor. §8 makes
/// broadcast unordered, so a client can see 105 before 100; treating the highest
/// value seen as a watermark would silently skip the operations in between.
/// Catch-up goes by version vector.
/// </para>
/// </remarks>
public sealed record OperationBroadcast(Guid DocumentId, byte[] Operations, long ServerSeq);
