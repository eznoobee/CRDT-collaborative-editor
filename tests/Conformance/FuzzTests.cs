using System.Globalization;
using System.Text.Json;
using Crdt.Simulation;

namespace Conformance;

/// <summary>
/// A fresh corpus every run (PROJECT_SPEC.md §9, §11 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// The committed corpus is a regression suite: the same thousand traces, every
/// run, catching what they caught before. This is the other half — traces
/// nobody has seen, drawn from a seed that changes each run, so the search
/// keeps moving. A fixed seed is a test rather than a fuzzer; it explores one
/// path forever, and its greenness after the first week means nothing.
/// </para>
/// <para>
/// The vacuity risks, named before this was written:
/// </para>
/// <list type="number">
/// <item>
/// <b>A fuzzer given a time budget passes by not finding anything.</b> So the
/// floor is a trace count and falling short FAILS rather than warns — a fuzzer
/// that gave up early and reported green is §13.20's defect exactly: a check
/// reporting on work it did not do.
/// </item>
/// <item>
/// <b>A failure nobody can reproduce is noise.</b> The seed is in the failure
/// message and the reproduction command is spelled out; the round trip is
/// exercised deliberately in <c>A_printed_seed_reproduces_its_corpus</c>,
/// because a recovery path nobody has walked is not a recovery path (§13.13).
/// </item>
/// <item>
/// <b>An advisory fuzz job is a guard that cannot fail</b> (§13.19) — it
/// verifies the job ran, not that the invariants hold. This runs in the same
/// blocking suite as everything else. A flake here is either genuine
/// nondeterminism in the cores or a trace whose expectation is stated wrongly;
/// both are defects, and §9 makes either a P0 corpus bug rather than a reason
/// to downgrade the job.
/// </item>
/// </list>
/// </remarks>
public sealed class FuzzTests
{
    /// <summary>The floor. Falling short fails; it never warns.</summary>
    private const int MinimumTraces = 250;

    /// <summary>
    /// A seed that changes every run, and is printed whatever happens.
    /// </summary>
    /// <remarks>
    /// Overridable through <c>CONFORMANCE_FUZZ_SEED</c>, which is how a
    /// reported failure is reproduced: the same value produces the same corpus.
    /// </remarks>
    private static int Seed()
    {
        var configured = Environment.GetEnvironmentVariable("CONFORMANCE_FUZZ_SEED");
        return configured is not null && int.TryParse(configured, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : Random.Shared.Next(1, int.MaxValue);
    }

    [Fact]
    public void Fresh_traces_converge_and_survive_the_wire()
    {
        var seed = Seed();
        var reproduce = $"CONFORMANCE_FUZZ_SEED={seed} dotnet run --project tests/Conformance";

        var replayed = 0;
        foreach (var trace in CorpusExport.Generate(seed, MinimumTraces))
        {
            using var doc = JsonDocument.Parse(CorpusExport.ToTraceJson(trace));
            var result = TraceReplay.Replay(doc.RootElement);

            foreach (var (replica, text) in result.ReplicaTexts)
            {
                Assert.True(
                    string.Equals(text, result.Text, StringComparison.Ordinal),
                    $"Replica {replica} diverged on {trace.Name} (scenario seed "
                    + $"{trace.Scenario.Seed}).\nReproduce: {reproduce}");
            }

            Assert.True(
                string.Equals(result.WireRoundTripText, result.Text, StringComparison.Ordinal),
                $"The wire round trip diverged on {trace.Name} (scenario seed "
                + $"{trace.Scenario.Seed}).\nReproduce: {reproduce}");

            replayed++;
        }

        // The floor, checked after the loop rather than assumed by it. A
        // generator that yielded early would otherwise leave this test green
        // having examined almost nothing.
        Assert.True(
            replayed >= MinimumTraces,
            $"Only {replayed} traces were replayed; §9 requires at least {MinimumTraces}. "
            + $"Corpus seed {seed}.");
    }

    [Fact]
    public void A_printed_seed_reproduces_its_corpus()
    {
        // The recovery path, walked. A seed printed in a failure is only useful
        // if feeding it back produces the same traces, and nothing else here
        // would notice if it did not.
        const int Seed_ = 987654321;

        var first = CorpusExport.Generate(Seed_, 25).ToArray();
        var second = CorpusExport.Generate(Seed_, 25).ToArray();

        Assert.Equal(CorpusExport.Digest(first), CorpusExport.Digest(second));

        // And a different seed must produce a different corpus, or "reproducible"
        // would be satisfied by a generator that ignores its seed entirely.
        Assert.NotEqual(
            CorpusExport.Digest(first),
            CorpusExport.Digest(CorpusExport.Generate(Seed_ + 1, 25).ToArray()));
    }
}
