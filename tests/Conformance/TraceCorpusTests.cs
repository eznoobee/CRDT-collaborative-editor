using System.Text.Json;
using System.Text.RegularExpressions;

namespace Conformance;

/// <summary>
/// Guards the shape of the shared trace corpus described in PROJECT_SPEC.md §9.
/// </summary>
/// <remarks>
/// The cross-implementation runner arrives with the two cores. What is enforced
/// here is the corpus contract: where traces live, the envelope they use, the
/// encoding rule from §6 that 64-bit values are decimal strings, and — most
/// importantly — that every trace explains itself. A trace whose expectation can
/// be edited without the editor noticing they are contradicting a paper is worse
/// than no trace.
/// </remarks>
public sealed class TraceCorpusTests
{
    private static readonly Regex CanonicalLowercaseUuid = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant);

    private static DirectoryInfo TraceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any())
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var traces = new DirectoryInfo(
            Path.Combine(dir.FullName, "tests", "Conformance", "traces"));
        Assert.True(traces.Exists, $"Trace directory not found: {traces.FullName}");
        return traces;
    }

    private static FileInfo[] Traces() =>
        [.. TraceDirectory().EnumerateFiles("*.json").OrderBy(f => f.Name, StringComparer.Ordinal)];

    private static IEnumerable<(FileInfo File, JsonElement Root)> Parsed()
    {
        foreach (var file in Traces())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
            yield return (file, doc.RootElement.Clone());
        }
    }

    [Fact]
    public void Corpus_is_not_empty()
    {
        // Guards against every schema test below passing vacuously.
        Assert.NotEmpty(Traces());
    }

    [Fact]
    public void Every_trace_has_the_expected_envelope()
    {
        foreach (var (file, root) in Parsed())
        {
            Assert.True(
                root.TryGetProperty("v", out var version)
                && version.ValueKind == JsonValueKind.Number,
                $"{file.Name}: missing numeric protocol version 'v' (§6).");

            Assert.True(
                root.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String,
                $"{file.Name}: missing string 'name'.");

            // The filename stem carries an ordering prefix; the name is the rest.
            var stem = Path.GetFileNameWithoutExtension(file.Name);
            var expectedName = stem[(stem.IndexOf('-', StringComparison.Ordinal) + 1)..];
            Assert.True(
                name.GetString() == expectedName,
                $"{file.Name}: 'name' is '{name.GetString()}' but the filename implies '{expectedName}'.");

            Assert.True(
                root.TryGetProperty("replicas", out var replicas)
                && replicas.ValueKind == JsonValueKind.Array
                && replicas.GetArrayLength() > 0,
                $"{file.Name}: 'replicas' must be a non-empty array.");

            var seenIndexes = new List<int>();
            foreach (var replica in replicas.EnumerateArray())
            {
                Assert.True(
                    replica.TryGetProperty("index", out var index)
                    && index.ValueKind == JsonValueKind.Number,
                    $"{file.Name}: every replica needs a numeric 'index'.");
                Assert.True(
                    replica.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && CanonicalLowercaseUuid.IsMatch(id.GetString()!),
                    $"{file.Name}: replica ids must be lowercase canonical UUIDs (§9).");
                seenIndexes.Add(index.GetInt32());
            }

            Assert.True(
                seenIndexes.SequenceEqual(Enumerable.Range(0, seenIndexes.Count)),
                $"{file.Name}: replica indexes must be 0..n-1 in order, got [{string.Join(", ", seenIndexes)}].");

            Assert.True(
                root.TryGetProperty("ops", out var ops)
                && ops.ValueKind == JsonValueKind.Array,
                $"{file.Name}: missing 'ops' array.");

            foreach (var op in ops.EnumerateArray())
            {
                Assert.True(
                    op.TryGetProperty("op", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() is "insert" or "delete" or "deliver" or "sync",
                    $"{file.Name}: every op needs 'op' of insert|delete|deliver|sync.");
            }
        }
    }

    [Fact]
    public void Every_trace_states_at_least_one_expectation()
    {
        foreach (var (file, root) in Parsed())
        {
            Assert.True(
                root.TryGetProperty("expected", out var expected)
                && expected.ValueKind == JsonValueKind.Object,
                $"{file.Name}: missing 'expected' object.");

            var hasText = expected.TryGetProperty("text", out var text)
                          && text.ValueKind == JsonValueKind.String;
            var hasOneOf = expected.TryGetProperty("oneOf", out var oneOf)
                           && oneOf.ValueKind == JsonValueKind.Array
                           && oneOf.GetArrayLength() > 0;
            var hasForbidden = expected.TryGetProperty("forbidden", out var forbidden)
                               && forbidden.ValueKind == JsonValueKind.Array
                               && forbidden.GetArrayLength() > 0;

            Assert.True(
                hasText || hasOneOf || hasForbidden,
                $"{file.Name}: 'expected' must carry at least one of text, oneOf, forbidden (§9).");

            // A permitted answer that is also forbidden would make the trace
            // unsatisfiable, which is a bug in the trace rather than the code.
            if (hasOneOf && hasForbidden)
            {
                var permitted = oneOf.EnumerateArray().Select(e => e.GetString()).ToHashSet(StringComparer.Ordinal);
                foreach (var bad in forbidden.EnumerateArray().Select(e => e.GetString()))
                {
                    Assert.False(
                        permitted.Contains(bad),
                        $"{file.Name}: '{bad}' appears in both oneOf and forbidden.");
                }
            }

            if (hasText && hasForbidden)
            {
                foreach (var bad in forbidden.EnumerateArray().Select(e => e.GetString()))
                {
                    Assert.False(
                        string.Equals(bad, text.GetString(), StringComparison.Ordinal),
                        $"{file.Name}: 'text' is also listed as forbidden.");
                }
            }
        }
    }

    [Fact]
    public void Every_trace_carries_a_rationale()
    {
        // PROJECT_SPEC.md §9. This is the test that stops a failing trace being
        // "fixed" by editing its expectation: the rationale names the paper
        // section, so contradicting it is visible in the diff.
        foreach (var (file, root) in Parsed())
        {
            var expected = root.GetProperty("expected");

            Assert.True(
                expected.TryGetProperty("rationale", out var rationale)
                && rationale.ValueKind == JsonValueKind.String,
                $"{file.Name}: 'expected.rationale' is required (§9).");

            var text = rationale.GetString()!;
            Assert.True(
                text.Length >= 40,
                $"{file.Name}: rationale is too short to say what the trace proves: '{text}'.");
        }
    }

    [Fact]
    public void Sixty_four_bit_values_are_encoded_as_decimal_strings()
    {
        // PROJECT_SPEC.md §6: JSON numbers are IEEE 754 doubles and do not
        // round-trip above 2^53, which would break the byte-identical
        // requirement in §9 silently and only after long uptime.
        foreach (var (file, root) in Parsed())
        {
            if (root.GetProperty("expected").TryGetProperty("versionVector", out var vv))
            {
                foreach (var entry in vv.EnumerateObject())
                {
                    Assert.True(
                        entry.Value.ValueKind == JsonValueKind.String,
                        $"{file.Name}: version vector entry '{entry.Name}' must be a "
                        + "decimal string, not a JSON number (§6).");
                }
            }
        }
    }
}
