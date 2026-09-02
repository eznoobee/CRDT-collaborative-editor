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
/// </remarks>
[Collection(nameof(PostgresTests))]
public sealed class SnapshotSizeMetricTests(PostgresFixture fixture, ITestOutputHelper output)
{
    private const int Elements = 100_000;

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
    private static List<ElementState> ForwardChain(ReplicaId replica, int count, int deletedEvery = 0)
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
                deletedEvery > 0 && i % deletedEvery == 0));
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

    [Fact]
    public async Task Reports_snapshot_size_and_load_time_for_a_large_document()
    {
        fixture.RequireDatabase();

        var cancellationToken = TestContext.Current.CancellationToken;
        var replicaId = ReplicaIdOf(1);

        var build = Stopwatch.StartNew();
        var replica = Replica.Import(
            replicaId,
            ForwardChain(replicaId, Elements),
            new Dictionary<ReplicaId, ulong> { [replicaId] = Elements });
        build.Stop();

        var serialise = Stopwatch.StartNew();
        var json = SnapshotSerializer.Serialize(replica);
        serialise.Stop();

        var deserialise = Stopwatch.StartNew();
        var restored = SnapshotSerializer.Deserialize(replicaId, json);
        deserialise.Stop();

        Assert.Equal(Elements, restored.Values.Count);

        var documentId = PostgresFixture.NewDocumentId();
        var store = new DocumentStore(fixture.DataSource);

        var write = Stopwatch.StartNew();
        await store.SaveSnapshotAsync(documentId, replica, Elements, cancellationToken);
        write.Stop();

        var load = Stopwatch.StartNew();
        var loaded = await store.LoadAsync(documentId, ReplicaIdOf(9), cancellationToken);
        load.Stop();

        Assert.Equal(Elements, loaded.Values.Count);

        var bytes = Encoding.UTF8.GetByteCount(json);
        void Report(string what, string value) =>
            output.WriteLine($"  {what,-34} {value}");

        output.WriteLine($"Snapshot metrics for {Elements:N0} live elements (§6, no threshold):");
        Report("serialised size", $"{bytes:N0} bytes ({bytes / 1024.0 / 1024.0:F2} MiB)");
        Report("bytes per element", (bytes / (double)Elements).ToString("F1", CultureInfo.InvariantCulture));
        Report("build (import)", $"{build.ElapsedMilliseconds:N0} ms");
        Report("serialise", $"{serialise.ElapsedMilliseconds:N0} ms");
        Report("deserialise", $"{deserialise.ElapsedMilliseconds:N0} ms");
        Report("write to Postgres", $"{write.ElapsedMilliseconds:N0} ms");
        Report("load from Postgres", $"{load.ElapsedMilliseconds:N0} ms");
        output.WriteLine(
            $"§8 targets a 100k-live-character document loading in under 500 ms server-side. "
            + $"Load here is {load.ElapsedMilliseconds:N0} ms with no tombstones and no operations "
            + "after the snapshot; §8's case adds 500k tombstones on top.");
    }
}
