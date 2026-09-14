using System.Net;

namespace Editor.Api.Tests;

/// <summary>
/// In-process checks of the API host. No Docker required.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public HealthEndpointTests(ApiFactory factory) =>
        _factory = factory;

    [Fact]
    public async Task Liveness_endpoint_responds_ok()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_refuses_rather_than_reporting_healthy_without_its_dependencies()
    {
        // THE SUCCESSOR TO A GUARD THAT DID ITS JOB. Until 7b.1 this test pinned
        // /health/ready's ABSENCE: §10 requires the endpoint and §12 forbids a
        // stub that reports healthy without checking anything, so the endpoint
        // was held out until the checks behind it were real. It failed the moment
        // readiness was added, which is exactly what it was for.
        //
        // Replaced rather than deleted, because the property it protected still
        // needs protecting and only its expression has changed. This host has no
        // Postgres and no Redis behind it — that is what makes it useful here —
        // so a readiness endpoint that answers OK from it is the hardcoded
        // return the original guard existed to prevent. It must refuse.
        //
        // The positive cases live in ReadinessTests, against a host that has both
        // dependencies and can have them taken away one at a time.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
