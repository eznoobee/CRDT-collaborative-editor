using Crdt.Simulation;

namespace Conformance;

/// <summary>
/// The generated corpus contract (PROJECT_SPEC.md §9, §11 Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// The vacuity risks, named before these were written:
/// </para>
/// <list type="number">
/// <item>
/// <b>1,000 traces is a count, and counts are satisfied by repetition.</b> So
/// the manifest records the distribution over §9's named dimensions, and a
/// dimension at zero fails — the same defect as "dashboards exist" (§13.22).
/// </item>
/// <item>
/// <b>A corpus generated to satisfy a metric satisfies the metric.</b> The
/// dimensions were named in §9 and measured in 5.1 before the generator was
/// touched, and 5.1's change was made because a dimension read zero, not
/// because a number wanted improving.
/// </item>
/// <item>
/// <b>"Reproducible from a seed" is an assumption unless something checks it.</b>
/// The digest is regenerated here and compared to the committed one, so a
/// generator change that silently alters the corpus fails rather than quietly
/// replacing what the manifest describes.
/// </item>
/// </list>
/// </remarks>
public sealed class GeneratedCorpusTests
{
    [Fact]
    public void The_committed_manifest_describes_what_the_seed_produces()
    {
        var manifest = GeneratedCorpus.ReadManifest();
        var traces = CorpusExport.Generate(manifest.Seed, manifest.Count).ToArray();

        Assert.Equal(manifest.Count, traces.Length);
        Assert.Equal(CorpusExport.GeneratorVersion, manifest.GeneratorVersion);

        Assert.Equal(manifest.Digest, CorpusExport.Digest(traces));
    }

    [Fact]
    public void Every_dimension_the_spec_names_is_reached_by_the_corpus()
    {
        var manifest = GeneratedCorpus.ReadManifest();
        var counts = CorpusDimensions.Count(
            CorpusExport.Generate(manifest.Seed, manifest.Count).Select(t => t.Scenario));

        var missing = CorpusDimensions.All.Where(d => counts[d] == 0).ToArray();
        Assert.True(
            missing.Length == 0,
            $"The corpus never reaches: {string.Join(", ", missing)}. §9 requires every "
            + "named dimension to be hit; a dimension at zero is a hole no count reports.");

        // And the manifest must say the same thing the corpus does, or it is a
        // description of a corpus that no longer exists.
        foreach (var dimension in CorpusDimensions.All)
        {
            Assert.True(
                manifest.Dimensions.TryGetValue(dimension, out var recorded),
                $"The manifest does not record {dimension}.");
            Assert.Equal(counts[dimension], recorded);
        }
    }

    [Fact]
    public void The_corpus_is_at_least_the_thousand_traces_the_phase_requires()
    {
        Assert.True(
            GeneratedCorpus.ReadManifest().Count >= 1_000,
            "§11's Phase 5 row requires 1,000 generated traces.");
    }
}
