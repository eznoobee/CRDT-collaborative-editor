namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// How big a generated scenario is, drawn as an explicit dimension rather than
/// falling out of the operation count.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §13.10. Until Phase 2.5 the generator's magnitudes were
/// literals — runs of two to four characters, at most five edits a round — so
/// every one of the 10,000 cases built a document of a few dozen elements. That
/// explores shape exhaustively and scale not at all, and it is why a stack
/// overflow at depth equal to document length survived eight invariants, 10,000
/// scenarios each, and an 87% mutation score.
/// </para>
/// <para>
/// Large scenarios are rare on purpose. They are the expensive ones —
/// <see cref="Replica.Insert"/> walks the document, so typing <c>n</c>
/// characters costs O(n²) — and their value is in being reached at all, not in
/// being reached often. A weighting that made them common would trade away the
/// shape coverage that finds ordering bugs for size coverage that finds one
/// class of bug repeatedly.
/// </para>
/// </remarks>
public sealed record ScenarioScale(
    string Name,
    int MaxPrefix,
    int MinRunLength,
    int MaxRunLength,
    int MaxEditsPerRound,
    int MaxRounds)
{
    /// <summary>The magnitudes the generator used before scale was a dimension.</summary>
    public static readonly ScenarioScale Small = new("small", 4, 2, 5, 6, 4);

    /// <summary>Hundreds of elements: past anything a hand-written test builds.</summary>
    public static readonly ScenarioScale Medium = new("medium", 40, 5, 40, 60, 5);

    /// <summary>
    /// Thousands of elements. Bounded by the O(n²) cost of generating through
    /// real typing; sizes past this are covered by <c>ScaleTests</c>, which
    /// build the same shape without paying it.
    /// </summary>
    public static readonly ScenarioScale Large = new("large", 400, 50, 400, 600, 4);

    /// <summary>
    /// Draws a scale. Small dominates; large appears roughly once in 250 cases,
    /// which is about forty times across the 10,000-case gate.
    /// </summary>
    public static ScenarioScale Draw(Random rng) => rng.Next(250) switch
    {
        0 => Large,
        < 13 => Medium,
        _ => Small,
    };
}
