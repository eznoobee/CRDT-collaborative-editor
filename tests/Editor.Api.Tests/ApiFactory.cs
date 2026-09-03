using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Editor.Api.Tests;

/// <summary>
/// Hosts the API in-process with an OIDC issuer configured.
/// </summary>
/// <remarks>
/// The host refuses to start without one (§7: no default issuer, no fallback),
/// so every in-process test has to supply it the way a deployment would — from
/// configuration, not from a default baked into the app. The issuer here is
/// unreachable on purpose: these tests exercise the host, not token validation,
/// and a test host that could reach a real identity provider would be one more
/// thing that fails for a reason unrelated to the code under test.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Issuer = "https://issuer.test.invalid/";
    public const string Audience = "editor-api";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = Issuer,
                ["Oidc:Audience"] = Audience,
                ["Oidc:MetadataAddress"] = Issuer + ".well-known/openid-configuration",

                // Also unreachable, and also fine: the multiplexer is built on
                // first use, so a host test that never touches Redis never
                // tries to connect. The tests that do need a real Redis take it
                // from RedisFixture instead.
                ["Redis:Configuration"] = "127.0.0.1:1,abortConnect=false,connectTimeout=1,connectRetry=0",

                // Likewise unreachable, and likewise fine: the DbContext opens
                // a connection when a request needs one, not at startup.
                ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=editor;Username=editor;Timeout=1",
            }));
    }
}
