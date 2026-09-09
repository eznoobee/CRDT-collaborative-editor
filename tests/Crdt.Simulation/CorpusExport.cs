using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crdt.Core;

namespace Crdt.Simulation;

/// <summary>
/// The generated half of §9's conformance corpus: a seed, and what it produces.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §9. Traces are emitted as **scripted executions in user
/// terms** — the same v1 envelope the hand-written traces use — so a generated
/// trace encodes intent rather than tree shape. That is what makes it legitimate
/// for one implementation to generate a corpus both implementations replay: the
/// trace says "insert 'a' at index 0", never "parent X, side right", so the
/// structure each core derives is still the thing under test.
/// </para>
/// <para>
/// No `expected` block. The hand-written traces carry expectations from the
/// papers and a required rationale; a generated trace has no paper behind it,
/// and inventing an expectation from whatever the generator's own core produced
/// would assert that the implementation agrees with itself. What generated
/// traces are for is the cross-implementation comparison, and that lives in the
/// diff of the two result files.
/// </para>
/// </remarks>
public static class CorpusExport
{
    /// <summary>
    /// Bumped when the generator's output changes for the same seed.
    /// </summary>
    /// <remarks>
    /// The manifest records it, so a corpus whose digest no longer matches can
    /// be told apart from one produced by a different generator — a digest
    /// mismatch alone does not distinguish "the generator changed" from "the
    /// manifest is stale".
    /// </remarks>
    public const int GeneratorVersion = 2;

    /// <summary>One generated trace, ready to serialise.</summary>
    public sealed record GeneratedTrace(string Name, Scenario Scenario);

    /// <summary>Generates <paramref name="count"/> traces from one seed.</summary>
    /// <remarks>
    /// Derived seeds rather than sequential integers: consecutive seeds produce
    /// correlated first draws in <see cref="Random"/>, so a corpus built from
    /// 0..999 is less varied than one built from a hash of each index — which
    /// is the kind of thing that would quietly thin a dimension out.
    /// </remarks>
    public static IEnumerable<GeneratedTrace> Generate(int seed, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var derived = Derive(seed, i);
            yield return new GeneratedTrace(
                $"generated-{i:D4}",
                ScenarioGenerator.Generate(derived));
        }
    }

    /// <summary>A per-trace seed, stable across runs and machines.</summary>
    public static int Derive(int seed, int index)
    {
        Span<byte> input = stackalloc byte[8];
        BitConverter.TryWriteBytes(input, seed);
        BitConverter.TryWriteBytes(input[4..], index);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        // Non-negative: Random's seed is an int and negative values are legal
        // but make a printed seed harder to paste back.
        return BitConverter.ToInt32(hash) & 0x7FFFFFFF;
    }

    /// <summary>Writes one trace in the §9 v1 envelope.</summary>
    public static string ToTraceJson(GeneratedTrace trace)
    {
        var scenario = trace.Scenario;
        var sb = new StringBuilder();
        sb.Append("{\n  \"v\": 1,\n  \"name\": \"").Append(trace.Name).Append("\",\n");
        sb.Append("  \"description\": \"Generated from seed ")
          .Append(scenario.Seed.ToString(CultureInfo.InvariantCulture))
          .Append(" at scale ").Append(scenario.Scale.Name)
          .Append(". Carries no expectation: what it proves is that both implementations ")
          .Append("derive the same document from the same intent (§9).\",\n");

        sb.Append("  \"replicas\": [\n");
        for (var i = 0; i < scenario.Replicas.Count; i++)
        {
            sb.Append("    { \"index\": ").Append(i).Append(", \"id\": \"")
              .Append(scenario.Replicas[i].ToString()).Append("\" }");
            sb.Append(i == scenario.Replicas.Count - 1 ? "\n" : ",\n");
        }

        sb.Append("  ],\n  \"ops\": [\n");
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            sb.Append("    ").Append(Step(scenario.Steps[i]));
            sb.Append(i == scenario.Steps.Count - 1 ? "\n" : ",\n");
        }

        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string Step(ScenarioStep step) => step switch
    {
        InsertStep s =>
            $"{{ \"op\": \"insert\", \"replica\": {s.Replica}, \"index\": {s.Index}, "
            + $"\"value\": {JsonSerializer.Serialize(s.Value.ToString())} }}",
        DeleteStep s => $"{{ \"op\": \"delete\", \"replica\": {s.Replica}, \"index\": {s.Index} }}",
        DeliverStep s => $"{{ \"op\": \"deliver\", \"from\": {s.From}, \"to\": {s.To} }}",
        SyncStep => "{ \"op\": \"sync\" }",
        _ => throw new InvalidOperationException($"Unknown step {step.GetType().Name}."),
    };

    /// <summary>
    /// A digest over the whole corpus, so "reproducible from a seed" is checked
    /// rather than assumed.
    /// </summary>
    public static string Digest(IEnumerable<GeneratedTrace> traces)
    {
        using var sha = SHA256.Create();
        foreach (var trace in traces)
        {
            var bytes = Encoding.UTF8.GetBytes(ToTraceJson(trace));
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}
