using System.Diagnostics;
using System.Globalization;
using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Persistence;

/// <summary>
/// Reports what a large snapshot costs (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// Metrics, not assertions. §6 asks for the number before the format reaches the
/// client's IndexedDB schema, because after Phase 4 changing it stops being
/// cheap. A threshold here would either be met trivially or fail the build for a
/// decision nobody has made yet.
/// </para>
/// <para>
/// The document is built by importing a synthetic element chain rather than by
/// typing. Typing is quadratic in this implementation — traversal-order queries
/// walk the whole tree — so 100k inserts would take minutes and would be
/// measuring insert throughput rather than snapshot cost. The construction is
/// checked against real typing at a small size first, so the synthetic chain is
/// known to be the shape typing actually produces.
/// </para>
/// <para>
/// <b>Two documents are measured, not one.</b> §6 requires it. The chain is the
/// best case the binary format has — one replica typing left to right collapses
/// to a single run record — and reporting it alone would overstate the format by
/// a wide margin. The fragmented case interleaves several replicas and deletes a
/// proportion of what they wrote, so most elements cannot join a run and pay the
/// full element-record price. The fragmented figure is the one to quote when
/// asking whether this reaches §8.
/// </para>
/// </remarks>
[Collection(nameof(PostgresTests))]
public sealed class SnapshotSizeMetricTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int Elements = 100_000;

    /// <summary>§8's stated case: 100k live characters and 500k tombstones.</summary>
    private const int StressElements = 600_000;

    private static ReplicaId ReplicaIdOf(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    /// <summary>
    /// The element shape produced by typing left to right: each character is a
    /// right child of the previous one, with no right origin because nothing
    /// followed it at the time.
    /// </summary>
    private static List<ElementState> ForwardChain(ReplicaId replica, int count)
    {
        var elements = new List<ElementState>(count);
        for (var i = 0; i < count; i++)
        {
            elements.Add(new ElementState(
                new ElementId(replica, (ulong)i),
                new Rune('a' + (i % 26)),
                i == 0 ? null : new ElementId(replica, (ulong)(i - 1)),
                Side.Right,
                null,
                IsDeleted: false));
        }

        return elements;
    }

    /// <summary>
    /// A deliberately run-hostile document: several replicas interleaved so that
    /// consecutive sequence numbers rarely sit next to each other in traversal
    /// order, with a proportion deleted.
    /// </summary>
    /// <remarks>
    /// Built directly rather than simulated, because what is wanted is a
    /// controlled worst-ish case rather than a realistic session — a session
    /// would be dominated by whichever replica typed the longest run, which is
    /// the case the chain already measures.
    /// </remarks>
    private static List<ElementState> Fragmented(int count, int replicas, int keepEvery)
    {
        var elements = new List<ElementState>(count);
        var seqs = new ulong[replicas];

        for (var i = 0; i < count; i++)
        {
            var replica = i % replicas;
            var id = new ElementId(ReplicaIdOf(replica + 1), seqs[replica]++);

            elements.Add(new ElementState(
                id,
                new Rune('a' + (i % 26)),
                i == 0 ? null : elements[i - 1].Id,
                Side.Right,
                // Every third element carries an explicit right origin, which is
                // also what stops it beginning a run (§6, §13.11).
                i > 2 && i % 3 == 0 ? elements[i - 2].Id : null,
                i % keepEvery != 0));
        }

        return elements;
    }

    [Fact]
    public void The_synthetic_chain_matches_what_typing_produces()
    {
        // Guards the metric below: if this construction stopped matching real
        // typing, the reported numbers would describe a document shape the
        // system never creates.
        var typed = new Replica(ReplicaIdOf(1));
        for (var i = 0; i < 20; i++)
        {
            typed.Insert(i, new Rune('a' + (i % 26)));
        }

        Assert.Equal(ForwardChain(ReplicaIdOf(1), 20), typed.Export());
    }

    [Theory]
    [InlineData("chain")]
    [InlineData("fragmented")]
    [InlineData("stress")]
    public async Task Reports_snapshot_size_and_load_time_for_a_large_document(string shape)
    {
        fixture.RequireDatabase();

        var cancellationToken = TestContext.Current.CancellationToken;
        var replicaId = ReplicaIdOf(1);

        var total = shape == "stress" ? StressElements : Elements;
        var (elements, vector) = shape switch
        {
            "chain" => (
                ForwardChain(replicaId, Elements),
                new Dictionary<ReplicaId, ulong> { [replicaId] = Elements }),
            "stress" => (
                // §8 exactly: 100k live characters and 500k tombstones, which §5
                // says cannot be collected on causal stability alone because a
                // RightOrigin can name one.
                Fragmented(StressElements, replicas: 4, keepEvery: 6),
                Enumerable.Range(1, 4).ToDictionary(
                    n => ReplicaIdOf(n), _ => (ulong)StressElements)),
            _ => (
                Fragmented(Elements, replicas: 4, keepEvery: 4),
                Enumerable.Range(1, 4).ToDictionary(
                    n => ReplicaIdOf(n), _ => (ulong)Elements)),
        };

        var build = Stopwatch.StartNew();
        var replica = Replica.Import(replicaId, elements, vector);
        build.Stop();

        var live = replica.Values.Count;

        var jsonSerialise = Stopwatch.StartNew();
        var json = SnapshotSerializer.Serialize(replica);
        jsonSerialise.Stop();

        var jsonDeserialise = Stopwatch.StartNew();
        SnapshotSerializer.Deserialize(replicaId, json);
        jsonDeserialise.Stop();

        var binarySerialise = Stopwatch.StartNew();
        var binary = SnapshotBinary.Encode(replica);
        binarySerialise.Stop();

        // Split, because the two halves have different fixes. Parsing is the
        // encoding's cost; placement is the algorithm rebuilding the tree, and
        // no encoding change touches it.
        var binaryParse = Stopwatch.StartNew();
        var parts = SnapshotBinary.DecodeParts(binary);
        binaryParse.Stop();

        var binaryPlace = Stopwatch.StartNew();
        var restored = Replica.Import(replicaId, parts.Elements, parts.VersionVector);
        binaryPlace.Stop();

        Assert.Equal(replica.Text, restored.Text);

        var documentId = PostgresFixture.NewDocumentId();
        var store = new DocumentStore(fixture.DataSource);

        var write = Stopwatch.StartNew();
        await store.SaveSnapshotAsync(documentId, replica, total, cancellationToken);
        write.Stop();

        var load = Stopwatch.StartNew();
        var loaded = await store.LoadAsync(documentId, ReplicaIdOf(9), cancellationToken);
        load.Stop();

        Assert.Equal(live, loaded.Values.Count);

        var jsonBytes = Encoding.UTF8.GetByteCount(json);
        void Report(string what, string value) =>
            output.WriteLine($"  {what,-34} {value}");

        static string PerElement(double bytes, int count) =>
            (bytes / count).ToString("F2", CultureInfo.InvariantCulture);

        output.WriteLine(
            $"Snapshot metrics, {shape} document, {total:N0} elements "
            + $"({live:N0} live) (§6, no threshold):");
        Report("JSON size", $"{jsonBytes:N0} bytes ({PerElement(jsonBytes, total)} per element)");
        Report(
            "binary size",
            $"{binary.Length:N0} bytes ({PerElement(binary.Length, total)} per element)");
        Report("binary is smaller by", $"{(double)jsonBytes / binary.Length:F1}x");
        Report("build (import)", $"{build.ElapsedMilliseconds:N0} ms");
        Report("JSON serialise", $"{jsonSerialise.ElapsedMilliseconds:N0} ms");
        Report("JSON deserialise", $"{jsonDeserialise.ElapsedMilliseconds:N0} ms");
        Report("binary serialise", $"{binarySerialise.ElapsedMilliseconds:N0} ms");
        Report("binary parse (bytes to elements)", $"{binaryParse.ElapsedMilliseconds:N0} ms");
        Report("binary place (elements to tree)", $"{binaryPlace.ElapsedMilliseconds:N0} ms");
        Report("write to Postgres", $"{write.ElapsedMilliseconds:N0} ms");
        Report("load from Postgres", $"{load.ElapsedMilliseconds:N0} ms");
        output.WriteLine(
            $"§8 targets a 100k-live-character document loading in under 500 ms server-side. "
            + $"Load here is {load.ElapsedMilliseconds:N0} ms; §8's case adds 500k tombstones "
            + "on top of 100k live characters.");
    }
}
