namespace Editor.Api.Hubs;

/// <summary>The outcome of a submission (PROJECT_SPEC.md §7).</summary>
/// <param name="Code">
/// <see langword="null"/> when accepted; otherwise one of <see cref="HubErrors"/>.
/// </param>
/// <param name="Accepted">How many operations were taken. Zero when rejected.</param>
/// <param name="RetryAfterMs">
/// For a throttle, how long to wait before resubmitting the same batch. Zero
/// for every other outcome — the server knows the window, and a client left to
/// invent the delay either hammers the limit it just hit or backs off for
/// longer than the window lasts.
/// </param>
/// <remarks>
/// §7 requires a hub failure to carry <c>not_found</c> or <c>forbidden</c>,
/// because a hub method has no status code and the 404-versus-403 distinction
/// has to survive the move to SignalR. It is a returned field rather than a
/// thrown <c>HubException</c> because SignalR does not deliver an exception's
/// message as the whole error: .NET 10 sends "An unexpected error occurred
/// invoking 'X' on the server. HubException: not_found", even with detailed
/// errors off. The code is in there, and a client that had to find it by
/// parsing an English sentence would be one framework revision from breaking —
/// with the failure mode being a client that cannot tell "you may not do this"
/// from "the server fell over".
/// </remarks>
public sealed record SubmitResult(string? Code, long Accepted, long RetryAfterMs = 0)
{
    /// <summary>A refusal carrying one of §7's codes and nothing else.</summary>
    public static SubmitResult Rejected(string code) => new(code, 0);

    /// <summary>
    /// §7's structured throttle response: the code, and when to come back.
    /// </summary>
    /// <remarks>
    /// The delay is part of the answer rather than a header or a guess,
    /// because §9 gives every refusal exactly one defined recovery and this
    /// one's recovery is "the same batch, later". A client left to invent the
    /// delay either hammers the limit it just hit or backs off for longer than
    /// the window, and the server is the only party that knows which.
    /// </remarks>
    public static SubmitResult Throttled(string code, TimeSpan retryAfter) =>
        new(code, 0, (long)retryAfter.TotalMilliseconds);

    /// <summary>An acceptance.</summary>
    public static SubmitResult Ok(long accepted) => new(null, accepted);
}
