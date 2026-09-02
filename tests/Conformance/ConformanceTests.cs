using System.Text.Json;

namespace Conformance;

/// <summary>
/// Replays every trace through the C# implementation, checks its stated
/// expectations, and writes the normalised result file (PROJECT_SPEC.md §9).
/// </summary>
/// <remarks>
/// This half of the harness proves the C# core satisfies the papers. Agreement
/// with the TypeScript core is a separate step over the two artefacts, because
/// two implementations can agree with each other while both being wrong.
/// </remarks>
public sealed class ConformanceTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any())
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir;
    }

    private static (FileInfo File, JsonElement Root)[] Traces()
    {
        var dir = new DirectoryInfo(
            Path.Combine(RepoRoot().FullName, "tests", "Conformance", "traces"));

        return
        [
            .. dir.EnumerateFiles("*.json")
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (f, JsonDocument.Parse(File.ReadAllText(f.FullName)).RootElement.Clone())),
        ];
    }

    [Fact]
    public void Every_trace_satisfies_its_stated_expectations()
    {
        foreach (var (file, root) in Traces())
        {
            var result = TraceReplay.Replay(root);
            var expected = root.GetProperty("expected");
            var rationale = expected.GetProperty("rationale").GetString();

            // Convergence is visible in the artefact, and asserted here so a
            // divergent trace names itself rather than showing up as a diff.
            foreach (var (replica, text) in result.ReplicaTexts)
            {
                Assert.True(
                    string.Equals(text, result.Text, StringComparison.Ordinal),
                    $"{file.Name}: replica {replica} has \"{text}\" but replica 0 has \"{result.Text}\".");
            }

            // §6: the encoding is a second implementation and must not lose
            // anything the algorithm depends on.
            Assert.True(
                string.Equals(result.WireRoundTripText, result.Text, StringComparison.Ordinal),
                $"{file.Name}: replaying through the wire encoding gave "
                + $"\"{result.WireRoundTripText}\" but direct replay gave \"{result.Text}\".");

            if (expected.TryGetProperty("text", out var exact))
            {
                Assert.True(
                    string.Equals(result.Text, exact.GetString(), StringComparison.Ordinal),
                    $"{file.Name}: expected \"{exact.GetString()}\", got \"{result.Text}\". {rationale}");
            }

            if (expected.TryGetProperty("oneOf", out var oneOf))
            {
                var permitted = oneOf.EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.True(
                    permitted.Contains(result.Text, StringComparer.Ordinal),
                    $"{file.Name}: \"{result.Text}\" is not one of "
                    + $"[{string.Join(", ", permitted.Select(p => $"\"{p}\""))}]. {rationale}");
            }

            if (expected.TryGetProperty("forbidden", out var forbidden))
            {
                var banned = forbidden.EnumerateArray().Select(e => e.GetString()).ToArray();
                Assert.False(
                    banned.Contains(result.Text, StringComparer.Ordinal),
                    $"{file.Name}: produced the forbidden result \"{result.Text}\". {rationale}");
            }
        }
    }

    [Fact]
    public void Writes_the_normalised_result_file()
    {
        var results = Traces().Select(t => TraceReplay.Replay(t.Root)).ToArray();
        var rendered = NormalisedResult.Render("csharp", results);

        var output = new DirectoryInfo(
            Path.Combine(RepoRoot().FullName, "artifacts", "conformance"));
        output.Create();

        File.WriteAllText(Path.Combine(output.FullName, "csharp.json"), rendered);

        Assert.StartsWith("{\n", rendered, StringComparison.Ordinal);
        Assert.EndsWith("}\n", rendered, StringComparison.Ordinal);
    }
}
