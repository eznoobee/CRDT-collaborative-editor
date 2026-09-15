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
    public async Task Readiness_endpoint_is_absent_until_its_dependencies_exist()
    {
        // PROJECT_SPEC.md §10 requires /health/ready, but §12 forbids a stub that
        // reports healthy without checking anything. Readiness must check Postgres
        // and Redis, which arrive in Phase 2. This test pins the endpoint's absence
        // so that when it appears it must appear with real checks behind it, and
        // fails loudly if someone adds a hardcoded one.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
