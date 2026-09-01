using System.Text.Json;

namespace Conformance;

/// <summary>
/// Guards the shape of the shared trace corpus described in PROJECT_SPEC.md §9.
/// </summary>
/// <remarks>
/// The cross-implementation runner arrives in Phase 1, once both cores exist.
/// What exists now is the corpus contract: where traces live, the envelope they
/// use, and the encoding rule from §6 that 64-bit values are decimal strings.
/// Pinning that here means the rule is enforced from the first real trace rather
/// than discovered when a counter passes 2^53.
/// </remarks>
public sealed class TraceCorpusTests
{
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

    [Fact]
    public void Corpus_is_not_empty()
    {
        // Guards against the schema tests below passing vacuously.
        Assert.NotEmpty(Traces());
    }

    [Fact]
    public void Every_trace_has_a_protocol_version_and_the_expected_envelope()
    {
        foreach (var file in Traces())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = doc.RootElement;

            Assert.True(
                root.TryGetProperty("v", out var version)
                && version.ValueKind == JsonValueKind.Number,
                $"{file.Name}: missing numeric protocol version 'v' (PROJECT_SPEC.md §6).");

            Assert.True(
                root.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String,
                $"{file.Name}: missing string 'name'.");

            Assert.True(
                root.TryGetProperty("ops", out var ops)
                && ops.ValueKind == JsonValueKind.Array,
                $"{file.Name}: missing 'ops' array.");

            Assert.True(
                root.TryGetProperty("expected", out var expected)
                && expected.ValueKind == JsonValueKind.Object,
                $"{file.Name}: missing 'expected' object.");

            Assert.True(
                expected.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String,
                $"{file.Name}: 'expected.text' must be a string.");

            Assert.True(
                expected.TryGetProperty("versionVector", out var vv)
                && vv.ValueKind == JsonValueKind.Object,
                $"{file.Name}: 'expected.versionVector' must be an object.");
        }
    }

    [Fact]
    public void Sixty_four_bit_values_are_encoded_as_decimal_strings()
    {
        // PROJECT_SPEC.md §6: JSON numbers are IEEE 754 doubles and do not
        // round-trip above 2^53, which would break the byte-identical
        // requirement in §9 silently and only after long uptime.
        foreach (var file in Traces())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file.FullName));

            var vv = doc.RootElement
                .GetProperty("expected")
                .GetProperty("versionVector");

            foreach (var entry in vv.EnumerateObject())
            {
                Assert.True(
                    entry.Value.ValueKind == JsonValueKind.String,
                    $"{file.Name}: version vector entry '{entry.Name}' must be a "
                    + "decimal string, not a JSON number (PROJECT_SPEC.md §6).");
            }

            foreach (var op in doc.RootElement.GetProperty("ops").EnumerateArray())
            {
                if (op.TryGetProperty("seq", out var seq))
                {
                    Assert.True(
                        seq.ValueKind == JsonValueKind.String,
                        $"{file.Name}: op 'seq' must be a decimal string (PROJECT_SPEC.md §6).");
                }
            }
        }
    }
}
