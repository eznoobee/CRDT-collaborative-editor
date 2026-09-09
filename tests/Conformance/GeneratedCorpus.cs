using System.Globalization;
using System.Text;
using System.Text.Json;
using Crdt.Simulation;

namespace Conformance;

/// <summary>
/// §9's generated corpus: produced from a committed seed, checked against a
/// committed manifest, and written where both runners can replay it.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §9, §11 Phase 5. The traces themselves are not committed —
/// a thousand files turn every generator change into a diff nobody reads, and a
/// diff nobody reads is a review that does not happen. The seed and the manifest
/// are committed instead, and the digest is what makes "reproducible from this
/// seed" a check rather than a claim.
/// </para>
/// <para>
/// The C# side generates and both sides replay. That is not a bias towards the
/// C# core: a trace is a scripted execution in user terms, so it says "insert
/// 'a' at index 0" and never names a parent, a side or an origin. Each
/// implementation still derives its own structure, which is the thing being
/// compared.
/// </para>
/// </remarks>
public sealed class GeneratedCorpus
{
    /// <summary>Where the manifest lives, committed.</summary>
    public const string ManifestFile = "tests/Conformance/generated-corpus.json";

    /// <summary>Where the traces are written, gitignored.</summary>
    public const string CorpusDirectory = "artifacts/conformance/generated";

    public sealed record Manifest(
        int V,
        int Seed,
        int Count,
        int GeneratorVersion,
        IReadOnlyDictionary<string, int> Dimensions,
        string Digest);

    public static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any())
        {
            dir = dir.Parent;
        }

        return dir ?? throw new InvalidOperationException("No .slnx above the test binary.");
    }

    public static Manifest ReadManifest()
    {
        var path = Path.Combine(RepoRoot().FullName, ManifestFile);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var dimensions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("dimensions").EnumerateObject())
        {
            dimensions[entry.Name] = entry.Value.GetInt32();
        }

        return new Manifest(
            root.GetProperty("v").GetInt32(),
            root.GetProperty("seed").GetInt32(),
            root.GetProperty("count").GetInt32(),
            root.GetProperty("generatorVersion").GetInt32(),
            dimensions,
            root.GetProperty("digest").GetString()!);
    }

    /// <summary>Renders a manifest in §9's pinned serialisation.</summary>
    public static string Render(Manifest manifest)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"v\": ").Append(manifest.V).Append(",\n");
        sb.Append("  \"seed\": ").Append(manifest.Seed).Append(",\n");
        sb.Append("  \"count\": ").Append(manifest.Count).Append(",\n");
        sb.Append("  \"generatorVersion\": ").Append(manifest.GeneratorVersion).Append(",\n");
        sb.Append("  \"dimensions\": {\n");

        var names = manifest.Dimensions.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < names.Length; i++)
        {
            sb.Append("    \"").Append(names[i]).Append("\": ")
              .Append(manifest.Dimensions[names[i]].ToString(CultureInfo.InvariantCulture));
            sb.Append(i == names.Length - 1 ? "\n" : ",\n");
        }

        sb.Append("  },\n");
        sb.Append("  \"digest\": \"").Append(manifest.Digest).Append("\"\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Generates the corpus the manifest describes and writes it to disk.</summary>
    public static CorpusExport.GeneratedTrace[] Materialise(Manifest manifest)
    {
        var traces = CorpusExport.Generate(manifest.Seed, manifest.Count).ToArray();
        var dir = new DirectoryInfo(Path.Combine(RepoRoot().FullName, CorpusDirectory));

        if (dir.Exists)
        {
            dir.Delete(recursive: true);
        }

        dir.Create();

        foreach (var trace in traces)
        {
            File.WriteAllText(
                Path.Combine(dir.FullName, $"{trace.Name}.json"),
                CorpusExport.ToTraceJson(trace));
        }

        return traces;
    }
}
