using System.Text;
using System.Text.Json;
using Crdt.Core;

namespace Conformance;

/// <summary>One replayed trace, in the normalised shape of PROJECT_SPEC.md §9.</summary>
public sealed record TraceResult(
    string Name,
    string Text,
    IReadOnlyDictionary<string, string> ReplicaTexts,
    IReadOnlyDictionary<string, string> VersionVector);

/// <summary>Replays a conformance trace against real replicas.</summary>
/// <remarks>
/// Traces are scripted executions in user terms, so this drives the same public
/// API a client would (§9). Nothing here inspects the tree.
/// </remarks>
public static class TraceReplay
{
    /// <summary>Replays one parsed trace document.</summary>
    public static TraceResult Replay(JsonElement trace)
    {
        var name = trace.GetProperty("name").GetString()!;

        var ids = trace.GetProperty("replicas")
            .EnumerateArray()
            .Select(r => ReplicaId.Parse(r.GetProperty("id").GetString()!))
            .ToArray();
        var replicas = ids.Select(id => new Replica(id)).ToArray();

        foreach (var op in trace.GetProperty("ops").EnumerateArray())
        {
            switch (op.GetProperty("op").GetString())
            {
                case "insert":
                    replicas[op.GetProperty("replica").GetInt32()].Insert(
                        op.GetProperty("index").GetInt32(),
                        SingleRune(op.GetProperty("value").GetString()!));
                    break;
                case "delete":
                    replicas[op.GetProperty("replica").GetInt32()]
                        .Delete(op.GetProperty("index").GetInt32());
                    break;
                case "deliver":
                    Deliver(
                        replicas[op.GetProperty("from").GetInt32()],
                        replicas[op.GetProperty("to").GetInt32()]);
                    break;
                case "sync":
                    SyncAll(replicas);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown op in trace '{name}'.");
            }
        }

        var replicaTexts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < replicas.Length; i++)
        {
            replicaTexts[ids[i].ToString()] = replicas[i].Text;
        }

        var versionVector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (replica, count) in replicas[0].VersionVector)
        {
            // §6: 64-bit values are decimal strings, never JSON numbers.
            versionVector[replica.ToString()] =
                count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new TraceResult(name, replicas[0].Text, replicaTexts, versionVector);
    }

    private static Rune SingleRune(string value)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length != 1)
        {
            throw new InvalidOperationException(
                $"A trace value must be exactly one code point, got '{value}' (§7).");
        }

        return runes[0];
    }

    private static void Deliver(Replica from, Replica to)
    {
        foreach (var op in from.OperationsSince(to.VersionVector))
        {
            to.Apply(op);
        }
    }

    private static void SyncAll(IReadOnlyList<Replica> replicas)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var from in replicas)
            {
                foreach (var to in replicas)
                {
                    if (!ReferenceEquals(from, to))
                    {
                        Deliver(from, to);
                    }
                }
            }
        }
    }
}
