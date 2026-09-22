using Crdt.Simulation;

namespace Conformance;

/// <summary>
/// Regenerates the committed manifest, only when explicitly asked.
/// </summary>
/// <remarks>
/// <para>
/// Gated on <c>CONFORMANCE_WRITE_MANIFEST</c> and skipped otherwise, so a
/// normal run — and every CI run — can only ever check the manifest, never
/// rewrite it. A manifest that regenerated itself whenever it disagreed would
/// not be a check at all; it would be a recording of whatever happened, which
/// is the rubber-stamp failure §13.21 describes for the mutation floor.
/// </para>
/// <para>
/// A Fact rather than a <c>Main</c> because this project's entry point belongs
/// to the test platform. The gate is what makes that safe.
/// </para>
/// </remarks>
public sealed class ManifestWriter
{
    /// <summary>The corpus §11's Phase 5 row asks for.</summary>
    private const int Seed = 20260904;
    private const int Count = 1_000;

    [Fact]
    public void Rewrite_the_manifest_when_asked()
    {
        var asked = Environment.GetEnvironmentVariable("CONFORMANCE_WRITE_MANIFEST");
        Assert.SkipUnless(
            asked == "1",
            "Set CONFORMANCE_WRITE_MANIFEST=1 to regenerate tests/Conformance/generated-corpus.json.");

        Write(Seed, Count);
    }

    private static void Write(int seed, int count)
    {
        var traces = CorpusExport.Generate(seed, count).ToArray();
        var counts = CorpusDimensions.Count(traces.Select(t => t.Scenario));

        var manifest = new GeneratedCorpus.Manifest(
            V: 1,
            Seed: seed,
            Count: count,
            GeneratorVersion: CorpusExport.GeneratorVersion,
            Dimensions: counts,
            Digest: CorpusExport.Digest(traces));

        var path = Path.Combine(GeneratedCorpus.RepoRoot().FullName, GeneratedCorpus.ManifestFile);
        File.WriteAllText(path, GeneratedCorpus.Render(manifest));

        Console.WriteLine($"Wrote {path}");
        foreach (var dimension in CorpusDimensions.All)
        {
            Console.WriteLine($"  {dimension,-32} {counts[dimension],6}  {(double)counts[dimension] / count,7:P1}");
        }
    }
}
