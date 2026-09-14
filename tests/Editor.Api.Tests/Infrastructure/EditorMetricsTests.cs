using Crdt.Core;
using Editor.Api.Documents;
using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's metric list, each one shown to move.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vacuity risk is the whole design of this file.</b> "The metric exists"
/// is satisfied by a counter registered and never incremented — §13.15's shape,
/// which this project has hit twice — so no test here asserts an instrument is
/// present. Each asserts a <i>value changed</i> in response to something a
/// person did.
/// </para><para>
/// <b>And the ones whose only states are zero and non-zero are tested in
/// pairs</b>, because "non-zero after the thing happened" is satisfied by a
/// counter that is always non-zero. A rejection counter that counted accepted
/// batches too would pass every single-case test.
/// </para><para>
/// <b>§13.32 applied to the list.</b> A metric keyed on submission covers
/// writers and nobody else, so the active-connection gauge is asserted against a
/// connection that never submits — the viewer, whose invisibility to
/// submission-keyed measurement is what let one reader hold the stability
/// frontier still for a whole phase.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class EditorMetricsTests
{
    private readonly EditorFixture _fixture;

    public EditorMetricsTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Submitting_moves_received_and_applied_and_not_rejected()
    {
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-writer", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "metrics-writer", documentId);

        var batch = client.Writer.Type("five!");
        Assert.Null((await client.SubmitAsync(batch)).Code);

        // Five code points in, five counted. Asserted as the number rather than
        // as "more than zero": a received counter incremented once per batch
        // would pass the weaker check and under-report by the run-expansion
        // factor exactly where §7's limits matter (3.6).
        Assert.Equal(5, metrics.Total("editor.operations.received"));
        Assert.Equal(5, metrics.Total("editor.operations.applied"));

        // The other half of the pair. A rejection counter that also counted
        // accepted batches would pass every test that only submits bad ones.
        Assert.Equal(0, metrics.Total("editor.operations.rejected"));
    }

    [Fact]
    public async Task A_refusal_moves_rejected_under_its_own_code_and_not_applied()
    {
        // Tagged, because an untagged rejection count tells an operator that
        // something is being refused and nothing about what — the difference
        // between a dashboard that diagnoses and one that exists (§13.22).
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-reject-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-viewer", Role.Viewer);

        await using var viewer = await DocumentClient.JoinAsync(factory, "metrics-viewer", documentId);

        Assert.NotNull((await viewer.SubmitAsync(viewer.Writer.Type("nope"))).Code);

        Assert.Equal(1, metrics.Total("editor.operations.rejected", "code", "forbidden"));

        // Nothing was applied, and nothing was received either: the refusal
        // happens before validation, so the operations never arrived in the
        // sense the received counter means.
        Assert.Equal(0, metrics.Total("editor.operations.applied"));
    }

    [Fact]
    public async Task Propagation_latency_is_recorded_once_per_accepted_batch()
    {
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-latency-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-latency", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "metrics-latency", documentId);

        Assert.Null((await client.SubmitAsync(client.Writer.Type("a"))).Code);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("b"))).Code);

        // Two batches, two observations. A histogram recorded once per process
        // or once per connection would still "exist" and would report a
        // distribution over the wrong population.
        Assert.Equal(2, metrics.Count("editor.propagation.latency"));

        // And the segment is receive→broadcast enqueue, so it is bounded above
        // by the round trip the client just waited out.
        Assert.True(
            metrics.Total("editor.propagation.latency") > 0,
            "propagation latency recorded a zero for a batch that did real work");
    }

    [Fact]
    public async Task The_active_connection_gauge_counts_a_viewer_who_never_submits()
    {
        // §13.32 applied to the metric list. A gauge derived from submissions
        // would report this document as having nobody on it.
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-gauge-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-watcher", Role.Viewer);

        metrics.Observe();
        var before = metrics.Latest("editor.connections.active");

        await using (var watcher = await DocumentClient.JoinAsync(factory, "metrics-watcher", documentId))
        {
            Assert.Null((await watcher.CatchUpAsync()).Code);

            metrics.Observe();
            Assert.True(
                metrics.Latest("editor.connections.active") > before,
                "the gauge did not count a connection that only reads");
        }
    }

    [Fact]
    public async Task Collecting_tombstones_moves_the_reclaimed_counter()
    {
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await CollectableAsync(factory, "metrics-gc-owner", "metrics-gc");

        await using var scope = factory.Services.CreateAsyncScope();
        var collected = await scope.ServiceProvider
            .GetRequiredService<ISnapshotGarbageCollector>()
            .CollectDocumentAsync(documentId, TestContext.Current.CancellationToken);

        Assert.True(collected > 0, "nothing was collectable, so this proves nothing");
        Assert.Equal(collected, metrics.Total("editor.gc.elements_collected"));
    }

    [Fact]
    public async Task A_sweep_that_collects_nothing_leaves_the_counter_alone()
    {
        // The pair for the GC counter. Without it, "the counter moved" is
        // satisfied by one that increments per sweep — which would report
        // steady reclamation on a system reclaiming nothing, the exact failure
        // §5's collector is hardest to see.
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-gc-empty");

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<ISnapshotGarbageCollector>()
            .CollectDocumentAsync(documentId, TestContext.Current.CancellationToken);

        Assert.Equal(0, metrics.Total("editor.gc.elements_collected"));
    }

    [Fact]
    public async Task Retiring_a_replica_moves_the_retired_counter()
    {
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

        await using var factory = new EditorApiFactory(
            _fixture, configure: services => services.AddSingleton<TimeProvider>(clock));

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-retire-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-retire", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "metrics-retire", documentId))
        {
            Assert.Null((await client.CatchUpAsync()).Code);
        }

        clock.Advance(TimeSpan.FromDays(8));
        var retired = await factory.Services.GetRequiredService<ReplicaRetirement>()
            .RetireAsync(TestContext.Current.CancellationToken);

        if (retired == 0)
        {
            // The hosted sweep races this one on the same clock; if it got
            // there first the counter still moved, which is what matters.
            Assert.True(
                metrics.Total("editor.replicas.retired") > 0,
                "no replica was retired by either sweep, so this proves nothing");
            return;
        }

        Assert.Equal(retired, metrics.Total("editor.replicas.retired"));
    }

    [Fact]
    public async Task Resync_required_has_a_counter_of_its_own()
    {
        // §9 makes this the one refusal that destroys a user's unsent work, so
        // its rate is a thing to alert on rather than a row in a breakdown of
        // rejections. 7.4 established it cannot arise while the log is intact,
        // so the frontier is constructed directly — the same honest setup that
        // task's tests use.
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-resync-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-resync", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "metrics-resync", documentId);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("abc"))).Code);

        await FrontierAsync(factory, documentId);

        var ghost = ReplicaIdConversion.FromGuid(Ghost);
        var orphan = OperationBinary.Encode(
        [
            new InsertOperation(
                new ElementId(
                    ReplicaIdConversion.FromGuid(client.Negotiated.ReplicaId),
                    client.Writer.NextSeq),
                new System.Text.Rune('x'),
                new ElementId(ghost, 20),
                Side.Right,
                null),
        ]);

        Assert.Equal(IngestRejection.ResyncRequired, (await client.SubmitAsync(orphan)).Code);

        Assert.Equal(1, metrics.Total("editor.resync_required"));
        Assert.Equal(
            1,
            metrics.Total("editor.operations.rejected", "code", IngestRejection.ResyncRequired));
    }

    private static readonly Guid Ghost = new("aaaaaaaa-0000-4000-8000-000000000001");

    private static async Task FrontierAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider
            .GetRequiredService<EditorDbContext>();

        var document = await context.Documents.SingleAsync(
            row => row.Id == documentId, TestContext.Current.CancellationToken);

        document.StabilityFrontier = new Dictionary<Guid, long> { [Ghost] = 50 };
        context.Entry(document).Property(row => row.StabilityFrontier).IsModified = true;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> CollectableAsync(
        EditorApiFactory factory, string owner, string typist)
    {
        var documentId = await DocumentSetup.DocumentAsync(factory, owner);
        await DocumentSetup.GrantAsync(factory, documentId, typist, Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, typist, documentId))
        {
            var typed = client.Writer.Type("collect me please");
            Assert.Null((await client.SubmitAsync(typed)).Code);
            client.ApplyLocal(typed);

            for (ulong seq = 9; seq <= 16; seq++)
            {
                var deleted = client.Writer.Delete(seq);
                Assert.Null((await client.SubmitAsync(deleted)).Code);
                client.ApplyLocal(deleted);
            }

            await client.AcknowledgeAsync();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider
            .GetRequiredService<EditorDbContext>();

        await context.DocumentReplicas
            .Where(row => row.DocumentId == documentId)
            .ExecuteUpdateAsync(
                row => row.SetProperty(r => r.RetiredAt, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);

        return documentId;
    }
}
