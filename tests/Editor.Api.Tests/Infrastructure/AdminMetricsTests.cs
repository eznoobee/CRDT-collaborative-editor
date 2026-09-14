using System.Net;
using System.Text.Json;
using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's readings, and the door they are behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The security claim is the one that needs a test.</b> <c>/metrics</c> is
/// bound to an admin listener §4's proxy does not forward to, which makes it
/// unreachable from outside the deployment by construction — and "by
/// construction" is a statement about configuration until something checks it.
/// The first test here fetches it on the application port and requires a 404.
/// </para><para>
/// <b>The vacuity risk on the rest.</b> "The endpoint answers" is satisfied by
/// an endpoint serving an empty object, which is §13.15 wearing a new hat: the
/// readings would exist, be readable, and say nothing. So what is asserted is
/// that a reading <i>moved in response to something a person did</i>, and that
/// two instances report different identities — which is the whole of what row 8
/// asks the dashboards to do.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class AdminMetricsTests
{
    private readonly EditorFixture _fixture;

    public AdminMetricsTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Metrics_are_not_reachable_on_the_application_port()
    {
        // THE ONE THAT MATTERS. Operational readings on the public listener
        // would be a route §4 publishes and §10 never meant anyone outside the
        // deployment to read.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            new Uri("/metrics", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_are_not_reachable_even_when_an_admin_port_is_configured()
    {
        // The pair. Without it, the test above passes because the endpoint is
        // not mapped at all in the default configuration — which proves the
        // feature is off, not that the door is shut. Here the endpoint exists,
        // and the application port still refuses it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture, settings: new() { ["Admin:Port"] = "9931" });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            new Uri("/metrics", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_reading_moves_when_somebody_submits()
    {
        // Not "the snapshot has a counters object". §10's instruments were
        // recorded for a whole task before anything could read them, and a
        // reader that reports zeros forever is the same failure one layer out.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        // NOT resolved before the traffic, deliberately. The first version of
        // this test fetched the snapshot from the container and then submitted,
        // which started the MeterListener as a side effect — so it passed
        // against a server where nothing constructs the snapshot until the
        // first scrape, and every counter recorded before that scrape was
        // invisible. The dashboards read zero for everything and it was reading
        // them, not this test, that found it.
        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-read-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-read", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "metrics-read", documentId))
        {
            Assert.Null((await client.SubmitAsync(client.Writer.Type("four"))).Code);
        }

        var snapshot = factory.Services.GetRequiredService<MetricsSnapshot>();

        // Four, not "more than zero": the listener has to have been running for
        // the whole submission, not to have caught the tail of it.
        Assert.Equal(4, Counter(snapshot.ToJson(), "editor.operations.applied"));
    }

    [Fact]
    public async Task A_histogram_reports_how_many_were_over_the_target()
    {
        // §8's question, which a count and a sum cannot answer: not "what was
        // the average" but "how many submissions were over 25 ms". The buckets
        // are what make the dashboard able to say a target is being missed
        // rather than that a number exists.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var snapshot = factory.Services.GetRequiredService<MetricsSnapshot>();

        var documentId = await DocumentSetup.DocumentAsync(factory, "metrics-hist-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "metrics-hist", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "metrics-hist", documentId))
        {
            Assert.Null((await client.SubmitAsync(client.Writer.Type("a"))).Code);
        }

        using var document = JsonDocument.Parse(snapshot.ToJson());
        var latency = document.RootElement
            .GetProperty("histograms")
            .GetProperty("editor.propagation.latency");

        Assert.True(latency.GetProperty("count").GetInt64() > 0);

        // Every bucket boundary §8's target sits among, present by name so a
        // dashboard reads them rather than guessing which exist.
        var buckets = latency.GetProperty("atOrBelow");
        foreach (var boundary in new[] { "1", "5", "10", "25", "50", "100", "250", "1000" })
        {
            Assert.True(
                buckets.TryGetProperty(boundary, out _),
                $"no bucket at {boundary} ms; §8's 25 ms target needs one either side of it");
        }

        Assert.True(latency.TryGetProperty("above", out _), "nothing counts the overflow");
    }

    [Fact]
    public async Task Readings_name_the_instance_they_came_from()
    {
        // Row 8: the dashboards must say WHICH INSTANCE. Two instances whose
        // readings are indistinguishable can report that a target is broken and
        // never say where, which is half the requirement missing.
        _fixture.RequireBoth();
        await using var one = new EditorApiFactory(
            _fixture, settings: new() { ["Instance:Id"] = "alpha" });
        await using var two = new EditorApiFactory(
            _fixture, settings: new() { ["Instance:Id"] = "beta" });

        using var first = JsonDocument.Parse(
            one.Services.GetRequiredService<MetricsSnapshot>().ToJson());
        using var second = JsonDocument.Parse(
            two.Services.GetRequiredService<MetricsSnapshot>().ToJson());

        Assert.Equal("alpha", first.RootElement.GetProperty("instance").GetString());
        Assert.Equal("beta", second.RootElement.GetProperty("instance").GetString());
    }

    private static double Counter(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("counters").TryGetProperty(name, out var value)
            ? value.GetDouble()
            : 0;
    }
}
