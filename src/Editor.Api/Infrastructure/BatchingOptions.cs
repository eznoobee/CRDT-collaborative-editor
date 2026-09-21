namespace Editor.Api.Infrastructure;

/// <summary>How submissions are coalesced before being written (§8).</summary>
/// <remarks>
/// Configuration rather than a constant for one reason: 9.4 had to measure the
/// adaptive default against §8's original fixed window across a load curve, and
/// two settings cannot be compared on one build unless one of them can be set.
/// The defaults here are <see cref="Editor.Infrastructure.Persistence.BatchingPolicy.Default"/>'s,
/// restated as numbers because configuration binding needs numbers — which is
/// also why `BatchingPolicyMatchesItsDefaults` asserts the two agree rather than
/// leaving a second copy of §8's figures to drift.
/// </remarks>
public sealed class BatchingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Batching";

    /// <summary>
    /// Extra milliseconds a drained batch waits for company. Zero is adaptive.
    /// </summary>
    public int WindowMs { get; set; }

    /// <summary>Operations after which a batch is written regardless.</summary>
    public int MaxOperations { get; set; } = 100;
}
