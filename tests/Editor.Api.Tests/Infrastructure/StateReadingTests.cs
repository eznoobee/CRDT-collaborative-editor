using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's readings that are counted from the rows (§13.44).
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because row 8's break was invisible.</b> The acknowledgement
/// counter read identically on an instance that wrote nothing and one that wrote
/// everything, because deleting the write left the increment beside it standing.
/// A reading derived from state cannot decouple from what it reports: there is
/// no statement to delete.
/// </para><para>
/// <b>The test that matters is the one below that deletes nothing.</b> It does
/// not sabotage the writer and check the gauge notices — that would be the same
/// mistake one level up, asserting against a break this file chose. It asserts
/// the gauge tracks the rows, and the rows are what the product writes.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class StateReadingTests
{
    private readonly EditorFixture _fixture;

    public StateReadingTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_replica_that_has_never_reported_counts_as_silent()
    {
        // THE READING ROW 8 NEEDED. A client that connects and never says what
        // it holds pins the frontier at nothing for its document, and until
        // this existed the only sign was a counter saying the acknowledgement
        // had arrived.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var readings = factory.Services.GetRequiredService<StateReadings>();
        await readings.ReadAsync(TestContext.Current.CancellationToken);
        var before = readings.SilentReplicas;

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-silent-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-silent", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-silent", documentId))
        {
            // Connects, types, and never reports — no acknowledge, no catch-up,
            // no vector on the submission. Exactly what row 8's broken instance
            // looked like from the database's side.
            Assert.Null((await client.SubmitAsync(client.Writer.Type("abc"))).Code);
        }

        await readings.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, readings.SilentReplicas);
    }

    [Fact]
    public async Task A_replica_that_reports_stops_counting_as_silent()
    {
        // The pair, and the one that stops "silent" being a count of replicas.
        // Without it the gauge could simply be LiveReplicas under another name,
        // which would rise identically and diagnose nothing.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var readings = factory.Services.GetRequiredService<StateReadings>();
        await readings.ReadAsync(TestContext.Current.CancellationToken);
        var before = readings.SilentReplicas;

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-heard-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-heard", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-heard", documentId))
        {
            Assert.Null((await client.SubmitReportingAsync(client.Writer.Type("abc"))).Code);
        }

        await readings.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before, readings.SilentReplicas);
    }

    [Fact]
    public async Task Live_replicas_are_counted_from_the_rows()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var readings = factory.Services.GetRequiredService<StateReadings>();
        await readings.ReadAsync(TestContext.Current.CancellationToken);
        var before = readings.LiveReplicas;

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-live-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-live", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-live", documentId))
        {
            Assert.Null((await client.CatchUpAsync()).Code);
        }

        await readings.ReadAsync(TestContext.Current.CancellationToken);

        // Still live after the socket closed, which is the point: §5 counts a
        // replica until it is retired, not until its tab is shut.
        Assert.Equal(before + 1, readings.LiveReplicas);
    }

    [Fact]
    public async Task The_readings_reach_the_gauges()
    {
        // §13.41 across one more boundary: the readings could be correct and
        // reported by nothing. The instruments are what a dashboard sees.
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        await factory.Services.GetRequiredService<StateReadings>()
            .ReadAsync(TestContext.Current.CancellationToken);

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-gauge-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-gauge", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-gauge", documentId))
        {
            Assert.Null((await client.CatchUpAsync()).Code);
        }

        await factory.Services.GetRequiredService<StateReadings>()
            .ReadAsync(TestContext.Current.CancellationToken);

        metrics.Observe();

        Assert.True(
            metrics.Latest("editor.replicas.live") > 0,
            "the live-replica gauge reported nothing after a replica was created");
    }

    [Fact]
    public async Task The_readings_start_before_anyone_scrapes()
    {
        // 7b.5's other lifecycle finding, kept from recurring: a reader that
        // begins when someone first asks reports nothing about everything that
        // happened before. StateReadings takes its first reading at startup,
        // and a gauge of zeros reads exactly like an empty database.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        // Nothing here calls ReadAsync. The hosted service does, at start.
        var readings = factory.Services.GetRequiredService<StateReadings>();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (readings.LiveReplicas == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(
            readings.LiveReplicas > 0,
            "nothing took a reading on its own; the gauges would be zero until the first scrape");
    }
}
