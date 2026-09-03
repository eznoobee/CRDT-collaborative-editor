namespace Editor.Api.Hubs;

/// <summary>What a reconnecting client is told (PROJECT_SPEC.md §8).</summary>
/// <param name="Code">
/// <see langword="null"/> when answered; otherwise one of <see cref="HubErrors"/>.
/// </param>
/// <param name="Snapshot">
/// §6 binary snapshot when the delta was not the cheaper answer, else
/// <see langword="null"/>.
/// </param>
/// <param name="Operations">§6 binary operation batch: the delta, or empty.</param>
/// <param name="ServerSeq">The highest <c>server_seq</c> this answer covers.</param>
/// <remarks>
/// Both payloads are opaque bytes in §6's formats. The transport frames them
/// and does not know what is inside (§6's constraint); a client that receives a
/// snapshot imports it and one that receives operations applies them, and
/// neither path is expressible in the transport's own type system.
/// </remarks>
public sealed record CatchUpResult(string? Code, byte[]? Snapshot, byte[] Operations, long ServerSeq)
{
    public static CatchUpResult Rejected(string code) => new(code, null, [], 0);
}
