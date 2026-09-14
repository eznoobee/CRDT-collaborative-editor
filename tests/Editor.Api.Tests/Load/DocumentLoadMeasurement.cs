using System.Diagnostics;
using System.Globalization;
using System.Text;
using Crdt.Core;
using Editor.Api.Tests.Persistence;
using Editor.Infrastructure.Persistence;
using Npgsql;

namespace Editor.Api.Tests.Load;

/// <summary>
/// §8's fourth target: document load &lt; 500 ms, server-side, 100k live
/// characters + 500k tombstones, cold cache, from snapshot + tail.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a steady state, and §8 says so.</b> "500k accumulated tombstones
/// implies GC is not keeping up — this is a stress target, not a steady state."
/// So nothing here collects first: the point of the case is a document §5's
/// collector has fallen behind on.
/// </para><para>
/// <b>"Cold" is qualified, because the honest version of it is not available
/// in-process.</b> Each load runs against a data source created for it and
/// disposed afterwards, so no pooled connection and no in-process state carries
/// over. What does carry over is Postgres's own page cache, which a fresh
/// connection does not touch — the snapshot row was just written and is still in
/// shared buffers. That makes every number here an <i>optimistic</i> reading of
/// cold, and a real cold start on a cold server would be slower. Stated rather
/// than quietly assumed, because a target measured under conditions kinder than
/// the ones it names is not measured.
/// </para><para>
/// <b>Snapshot plus tail, not snapshot alone.</b> §8's measurement point says
/// "from snapshot + tail", and a load that reads only a snapshot skips the
/// replay half of <see cref="DocumentStore.LoadAsync"/> entirely — which is the
/// half that grows without bound between sweeps.
/// </para>
/// </remarks>
[Collection(nameof(PostgresTests))]
public sealed class DocumentLoadMeasurement(PostgresFixture fixture, ITestOutputHelper output)
{
    /// <summary>§8's stated case: 100k live characters and 500k tombstones.</summary>
    private const int StressElements = 600_000;

    /// <summary>Keeps one element in six, which is 100k live of 600k.</summary>
    private const int KeepEvery = 6;

    private const int Replicas = 4;

    /// <summary>Operations appended after the snapshot, replayed on every load.</summary>
    /// <remarks>
    /// A minute of one person typing at eight characters a second, which is
    /// about what sits behind a snapshot taken every 500 operations under §6's
    /// sweep. Making it larger would measure the replay rather than the load.
    /// </remarks>
    private const int TailOperations = 480;

    /// <summary>Cold loads measured.</summary>
    /// <remarks>
    /// §8 states a threshold rather than a percentile for this target, so what
    /// is reported is the distribution over repeated cold loads with the count
    /// attached. Twenty is few — the p99 of twenty is the worst of twenty, and
    /// the report says so rather than dressing it as a tail.
    /// </remarks>
    private const int Loads = 20;

    private void Report(string line)
    {
        output.WriteLine(line);
        Results.Write(line);
    }

    private static ReplicaId ReplicaIdOf(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    /// <summary>
    /// The run-hostile shape <c>SnapshotSizeMetricTests</c> measures, kept
    /// identical so the two figures describe the same document.
    /// </summary>
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
                i > 2 && i % 3 == 0 ? elements[i - 2].Id : null,
                i % keepEvery != 0));
        }

        return elements;
    }

    [Fact]
    public async Task Server_side_document_load()
    {
        LoadGate.Require();
        fixture.RequireDatabase();

        var cancellationToken = TestContext.Current.CancellationToken;
        var provenance = Provenance.Current();

        var elements = Fragmented(StressElements, Replicas, KeepEvery);
        var vector = Enumerable.Range(1, Replicas)
            .ToDictionary(n => ReplicaIdOf(n), _ => (ulong)StressElements);

        var build = Stopwatch.StartNew();
        var replica = Replica.Import(ReplicaIdOf(1), elements, vector);
        build.Stop();

        var live = replica.Values.Count;

        var documentId = PostgresFixture.NewDocumentId();
        var store = new DocumentStore(fixture.DataSource);
        var writer = new OperationLogWriter(fixture.DataSource);

        // At sequence zero, and the tail above it. The first version stamped the
        // snapshot at 600,000 — the element count, which is not a server_seq —
        // while the log was empty, so the tail was assigned 1..480, landed
        // BELOW the snapshot, and LoadAsync skipped all of it. It reported a
        // fast load of a document missing its tail, which is §13.19 arriving as
        // a performance result: the number was good because the work had not
        // been done. The element-count assertion below is what caught it, and
        // is why it is inside the loop rather than after it.
        //
        // The invariant the mistake ran into is §6's, and the periodic
        // snapshotter holds it deliberately: a snapshot's sequence is the head
        // of the log at the moment its state was taken, not a count of anything.
        await store.SaveSnapshotAsync(documentId, replica, 0, cancellationToken);

        var tailAuthor = ReplicaIdOf(9);
        var tail = new List<Operation>(TailOperations);
        ElementId? previous = null;
        for (var i = 0; i < TailOperations; i++)
        {
            var id = new ElementId(tailAuthor, (ulong)i);
            tail.Add(new InsertOperation(id, new Rune('a' + (i % 26)), previous, Side.Right, null));
            previous = id;
        }

        await writer.AppendAsync(documentId, tail, cancellationToken);

        var connectionString = fixture.DataSource!.ConnectionString;
        var samples = new List<double>(Loads);

        // Measured rather than guessed. A load allocates a 100k-element replica,
        // so a spread across identical loads has two candidate causes — the
        // database and the collector — and the pause time separates them. This
        // is the in-process caveat again: the pause is real work the server
        // would also do, but it is charged here against a process that is also
        // holding the built document.
        var pauses = new List<double>(Loads);

        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();

        for (var i = 0; i < Loads; i++)
        {
            // A data source per load. Reusing one would measure the second load
            // against a warm pooled connection, and every load after the first
            // would be a different measurement wearing the same name.
            await using var cold = NpgsqlDataSource.Create(connectionString);
            var coldStore = new DocumentStore(cold);

            var pauseBefore = GC.GetTotalPauseDuration();
            var load = Stopwatch.StartNew();
            var loaded = await coldStore.LoadAsync(documentId, ReplicaIdOf(8), cancellationToken);
            load.Stop();
            pauses.Add((GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);

            // Checked every time. A load that returned an empty document would
            // be very fast and would pass a threshold it has no business
            // passing — §13.19 arriving as a performance result.
            Assert.Equal(live + TailOperations, loaded.Values.Count);

            samples.Add(load.Elapsed.TotalMilliseconds);
        }

        wall.Stop();
        var utilisation = Utilisation.Since(cpu, wall);
        var loads = Percentiles.Of(samples);

        Report("§8 target 4 — server-side document load < 500 ms");
        Report($"  build      {provenance}");
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"  document   {StressElements:N0} elements, {live:N0} live, "
            + $"{StressElements - live:N0} tombstones, {TailOperations} operations of tail"));
        Report($"  load ms    {loads}");
        Report($"  gc pause   {Percentiles.Of(pauses)}");
        Report($"  generator  {utilisation}");
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"  note       cold = fresh data source per load; Postgres' own page cache stays warm, "
            + $"so this is an optimistic reading of cold"));
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"  aside      building the replica in memory took {build.ElapsedMilliseconds:N0} ms, "
            + $"which is not part of the target and is where §13.9's browser figure goes"));

        Assert.Equal(Loads, loads.Count);

        Assert.True(
            loads.P50 < 500,
            $"§8's target is < 500 ms; measured {loads} over {loads.Count} cold loads");

        Assert.True(
            loads.Max < 500,
            $"§8's target is < 500 ms and the worst of {loads.Count} cold loads was {loads.Max:F0} ms");
    }
}
