using System.Globalization;
using System.Text;
using Crdt.Core.Tests.Simulation;
using Xunit.Abstractions;

namespace Crdt.Core.Tests;

/// <summary>
/// Scale as a first-class dimension (PROJECT_SPEC.md §13.10).
/// </summary>
/// <remarks>
/// <para>
/// The rest of the suite explores <em>shape</em>: which replica inserted where,
/// in what order, with what delivered to whom. It explored <em>size</em> not at
/// all, and a stack overflow at depth equal to document length survived eight
/// invariants at 10,000 cases each and an 87% mutation score because of it.
/// Typing left to right is the most ordinary thing a user does, and a generator
/// that randomises shape produces balanced structures — it actively avoids the
/// one shape that breaks.
/// </para>
/// <para>
/// These cases are few and large, which is the opposite of the property suite
/// and deliberately so. Their value is in being reached at all.
/// </para>
/// </remarks>
public sealed class ScaleTests(ITestOutputHelper output)
{
    private static Rune Letter(int i) => new('a' + (i % 26));

    [Fact]
    public void Large_scale_is_reachable_within_the_case_budget()
    {
        // The generator draws its scale before anything else, so this is exactly
        // the draw Generate makes — without paying to generate 10,000 scenarios.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstLarge = -1;

        for (var seed = 0; seed < PropertyRunner.DefaultCases; seed++)
        {
            var scale = ScenarioScale.Draw(new Random(seed));
            counts[scale.Name] = counts.GetValueOrDefault(scale.Name) + 1;
            if (firstLarge < 0 && scale == ScenarioScale.Large)
            {
                firstLarge = seed;
            }
        }

        foreach (var (name, count) in counts.OrderByDescending(p => p.Value))
        {
            output.WriteLine($"  {name,-8} {count,6} of {PropertyRunner.DefaultCases}");
        }

        // A dimension that is never drawn is not a dimension. This asserts the
        // weighting rather than trusting it: make Large rare enough and the
        // 10,000-case gate stops covering scale while still claiming to.
        Assert.True(
            firstLarge >= 0,
            $"No large scenario in {PropertyRunner.DefaultCases} seeds; the weighting in "
            + "ScenarioScale.Draw has made the dimension unreachable.");
        output.WriteLine($"  first large at seed {firstLarge}");
    }

    [Fact]
    public void Invariants_hold_at_large_scale()
    {
        // The explicit-scale overload exists for this: demand the size rather
        // than generate hundreds of cases hoping to be dealt one.
        var largest = 0;
        var seeds = ScaleBudget.IsReduced ? 3 : 20;

        for (var seed = 0; seed < seeds; seed++)
        {
            var scenario = ScenarioGenerator.Generate(seed, ScenarioScale.Large);
            var result = SimulationRunner.Run(scenario);

            Assert.True(
                result.Converged,
                $"Replicas disagreed at large scale.\n{scenario.Describe()}");

            var elements = result.Replicas[0].AllIds.Count;
            largest = Math.Max(largest, elements);

            // Export and import at this size exercises the placement replay that
            // was quadratic until the 100k metric found it (§13.9).
            var restored = Replica.Import(
                result.Replicas[0].Id,
                result.Replicas[0].Export(),
                result.Replicas[0].VersionVector);
            Assert.Equal(result.Replicas[0].Text, restored.Text);
        }

        output.WriteLine($"largest document across {seeds} large scenarios: {largest:N0} elements");
        Assert.True(largest >= 500, $"Large scale produced only {largest} elements.");
    }

    [Fact]
    public void Typing_ten_thousand_characters_left_to_right_converges()
    {
        // The escaped bug, at the largest size real typing can afford: Insert
        // walks the document, so this is already O(n²). Bigger sequential cases
        // below construct the same shape without paying that, which is sound
        // because ReplicaTests pins the two constructions to each other.
        var length = Math.Min(10_000, ScaleBudget.MaxElements);
        var author = new Replica(ReplicaIds.Numbered(1));
        var reader = new Replica(ReplicaIds.Numbered(2));

        for (var i = 0; i < length; i++)
        {
            reader.Apply(author.Insert(i, Letter(i)));
        }

        Assert.Equal(length, author.Values.Count);
        Assert.Equal(author.Text, reader.Text);
        Assert.Equal(length, reader.AllIds.Count);
    }

    [Theory]
    [InlineData(50_000)]
    [InlineData(150_000)]
    public void A_sequential_document_survives_export_and_import(int requested)
    {
        var length = Math.Min(requested, ScaleBudget.MaxElements);

        // Depth equals length here, so every traversal in this test is the one
        // that overflowed the stack. On .NET a stack overflow cannot be caught —
        // reintroducing the recursive walk kills the test host rather than
        // failing an assertion. That is still a red build, and it is the only
        // signal available; the TypeScript side throws a catchable RangeError
        // and its regression test asserts on it directly.
        var replicaId = ReplicaIds.Numbered(1);
        var elements = new List<ElementState>(length);
        for (var i = 0; i < length; i++)
        {
            elements.Add(new ElementState(
                new ElementId(replicaId, (ulong)i),
                Letter(i),
                i == 0 ? null : new ElementId(replicaId, (ulong)(i - 1)),
                Side.Right,
                null,
                IsDeleted: false));
        }

        var replica = Replica.Import(
            replicaId, elements, new Dictionary<ReplicaId, ulong> { [replicaId] = (ulong)length });

        Assert.Equal(length, replica.Values.Count);
        Assert.Equal(length, replica.AllIds.Count);

        var exported = replica.Export();
        Assert.Equal(length, exported.Count);
        Assert.Equal(
            elements[length - 1].Id,
            exported[length - 1].Id);

        output.WriteLine(
            $"{length.ToString("N0", CultureInfo.InvariantCulture)} elements: "
            + "imported, traversed and exported.");
    }
}
