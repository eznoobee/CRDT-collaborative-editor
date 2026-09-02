using System.Text;
using System.Text.Json;
using Crdt.Core;
using Editor.Infrastructure.Serialization;

namespace Conformance;

/// <summary>One replayed trace, in the normalised shape of PROJECT_SPEC.md §9.</summary>
public sealed record TraceResult(
    string Name,
    string Text,
    IReadOnlyDictionary<string, string> ReplicaTexts,
    IReadOnlyDictionary<string, string> VersionVector,
    string WireRoundTripText,
    string Snapshot);

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

        var produced = new List<Operation>();

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
                    produced.Add(replicas[op.GetProperty("replica").GetInt32()].Insert(
                        op.GetProperty("index").GetInt32(),
                        SingleRune(op.GetProperty("value").GetString()!)));
                    break;
                case "delete":
                    produced.Add(replicas[op.GetProperty("replica").GetInt32()]
                        .Delete(op.GetProperty("index").GetInt32()));
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

        return new TraceResult(
            name,
            replicas[0].Text,
            replicaTexts,
            versionVector,
            WireRoundTrip(ids[0], produced),
            SnapshotHex(replicas[0]));
    }

    /// <summary>
    /// Replays the same operations after a trip through each encoding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PROJECT_SPEC.md §6: each encoding is a second implementation alongside its
    /// TypeScript counterpart, so both are exercised on every trace rather than
    /// on one. Anything an encoding loses — a right origin that meant
    /// end-of-document, a side, a sequence number past 2^53 — changes the text
    /// this produces.
    /// </para>
    /// <para>
    /// Both forms are replayed: JSON because it is normative, and binary because
    /// it is what actually travels. They are compared to each other before being
    /// returned, so a failure names which encoding lost something instead of
    /// surfacing as a text mismatch with no attribution.
    /// </para>
    /// </remarks>
    private static string WireRoundTrip(ReplicaId id, IReadOnlyList<Operation> operations)
    {
        var viaJson = new Replica(id);
        foreach (var operation in operations)
        {
            viaJson.Apply(OperationWireFormat.Decode(OperationWireFormat.Encode(operation)));
        }

        var viaBinary = new Replica(id);
        foreach (var operation in OperationBinary.Decode(OperationBinary.Encode(operations)))
        {
            viaBinary.Apply(operation);
        }

        if (!string.Equals(viaJson.Text, viaBinary.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The JSON wire form replays to \"{viaJson.Text}\" and the binary wire form to "
                + $"\"{viaBinary.Text}\"; one of the two encodings loses something (§6).");
        }

        return viaBinary.Text;
    }

    /// <summary>
    /// The binary snapshot as lowercase hex, after checking that binary and the
    /// normative JSON agree about this document in both directions.
    /// </summary>
    /// <remarks>
    /// PROJECT_SPEC.md §6: binary is the storage form and JSON is what a correct
    /// serialisation <em>is</em>. These two round trips are what tie them
    /// together. Without them binary would be a second definition of correctness
    /// that nothing checks against the first, and the two would drift the way
    /// any unchecked pair of implementations drifts.
    ///
    /// The hex then goes into the artefact, so a C#/TypeScript disagreement
    /// about the bytes fails the build exactly as an algorithm disagreement
    /// does — which is the whole point of putting it there rather than
    /// asserting it locally on each side.
    /// </remarks>
    private static string SnapshotHex(Replica replica)
    {
        var binary = SnapshotBinary.Encode(replica);
        var json = SnapshotSerializer.Serialize(replica);

        // binary -> JSON -> binary
        var fromBinary = SnapshotBinary.Decode(replica.Id, binary);
        var reBinary = SnapshotBinary.Encode(
            SnapshotSerializer.Deserialize(replica.Id, SnapshotSerializer.Serialize(fromBinary)));
        if (!reBinary.AsSpan().SequenceEqual(binary))
        {
            throw new InvalidOperationException(
                "binary -> JSON -> binary is not byte-identical, so the binary form and the "
                + "normative form disagree about this document (§6).");
        }

        // JSON -> binary -> JSON
        var fromJson = SnapshotSerializer.Deserialize(replica.Id, json);
        var reJson = SnapshotSerializer.Serialize(
            SnapshotBinary.Decode(replica.Id, SnapshotBinary.Encode(fromJson)));
        if (!string.Equals(reJson, json, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JSON -> binary -> JSON is not byte-identical (§6).");
        }

        return Convert.ToHexString(binary).ToLowerInvariant();
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
