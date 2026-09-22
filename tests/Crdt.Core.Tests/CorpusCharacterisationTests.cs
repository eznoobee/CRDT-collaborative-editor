using Crdt.Simulation;

namespace Crdt.Core.Tests;

/// <summary>
/// What the generator actually produces (PROJECT_SPEC.md §9, §11 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// §13.10 found this generator exploring shape exhaustively and scale not at
/// all, and nothing reported that until a stack overflow did. These tests ask
/// the same question of every other dimension §9 names, before the corpus is
/// grown: a corpus that never reaches a shape cannot be said to cover it, and
/// the number of traces says nothing either way.
/// </para>
/// <para>
/// The vacuity risks, named before these were written:
/// </para>
/// <list type="number">
/// <item>
/// <b>A histogram of the generator's own parameters tests the parameters.</b>
/// A weight of 0.45 for run sessions is an intention; the generator drops
/// sessions when the document is too small, and only counting the output can
/// find that. Every measurement here is on the produced <see cref="Scenario"/>.
/// </item>
/// <item>
/// <b>"Coverage" as line coverage of the core would read ~100% from ten
/// scenarios and mean nothing.</b> The dimensions are §5's, counted
/// individually, and a dimension at zero is a failure rather than a note.
/// </item>
/// <item>
/// <b>An assertion that every dimension is non-zero passes on a corpus that
/// reaches each one once.</b> So the rarest dimension carries a floor as a
/// proportion, and the floors are recorded here at values the current generator
/// clears — which makes a later regression visible rather than a later
/// discovery of a hole.
/// </item>
/// </list>
/// </remarks>
public sealed class CorpusCharacterisationTests(ITestOutputHelper output)
{
    /// <summary>Enough to make proportions meaningful, small enough to stay fast.</summary>
    private const int Sample = 2_000;

    private static Scenario[] Corpus() =>
        [.. Enumerable.Range(0, Sample).Select(ScenarioGenerator.Generate)];

    [Fact]
    public void Every_named_dimension_is_reached()
    {
        var counts = CorpusDimensions.Count(Corpus());

        var missing = counts.Where(entry => entry.Value == 0).Select(entry => entry.Key).ToArray();
        Assert.True(
            missing.Length == 0,
            $"The generator never produces: {string.Join(", ", missing)}. "
            + "A dimension §9 names and the corpus never reaches is a hole no count of "
            + "traces reports (§13.27).");
    }

    [Theory]
    // Floors, not targets. Each is set below what the generator currently
    // produces and above zero, so the test fails on a regression that thins a
    // dimension out rather than only on one that removes it entirely.
    [InlineData(CorpusDimensions.ConcurrentAtOnePosition, 0.30)]
    [InlineData(CorpusDimensions.InterleavingPressure, 0.20)]
    [InlineData(CorpusDimensions.SingleCharacterConcurrency, 0.05)]
    [InlineData(CorpusDimensions.BackwardRun, 0.10)]
    [InlineData(CorpusDimensions.DeleteOfConcurrentInsert, 0.05)]
    [InlineData(CorpusDimensions.CausallyDelayedDelivery, 0.05)]
    [InlineData(CorpusDimensions.ThreeOrMoreReplicas, 0.40)]
    public void Each_dimension_holds_its_floor(string dimension, double floor)
    {
        var counts = CorpusDimensions.Count(Corpus());
        var proportion = (double)counts[dimension] / Sample;

        Assert.True(
            proportion >= floor,
            $"{dimension} appears in {proportion:P1} of {Sample} scenarios, below the {floor:P0} floor. "
            + "Either the generator changed or the measurement did; both are worth knowing.");
    }

    [Fact]
    public void The_distribution_is_reported_and_not_merely_asserted()
    {
        // §9: reported, not merely computed. A floor that passes tells nobody
        // what the corpus looks like, and the shape is what a reviewer needs in
        // order to notice that one dimension is scraping through at 5%.
        var counts = CorpusDimensions.Count(Corpus());

        foreach (var dimension in CorpusDimensions.All)
        {
            var count = counts[dimension];
            output.WriteLine($"{dimension,-32} {count,6}  {(double)count / Sample,7:P1}");
        }

        Assert.Equal(CorpusDimensions.All.Count, counts.Count);
    }
}
