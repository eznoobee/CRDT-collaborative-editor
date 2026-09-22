using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Editor.Api.Tests.Authentication;

/// <summary>
/// The §7 properties that are about the whole host rather than one options object.
/// </summary>
/// <remarks>
/// Each extension method has its own unit tests proving it validates what it
/// should. These prove the app calls them, and calls them somewhere that runs
/// before the first request. Wiring a validator up and never invoking it leaves
/// every unit test green and every deployment misconfigured.
/// </remarks>
public sealed class HostStartupTests
{
    [Fact]
    public void The_host_refuses_to_start_with_no_issuer_configured()
    {
        // appsettings.json carries no Oidc section, so this is the real default
        // configuration of a deployment that forgot to set one.
        var thrown = Record.Exception(() => Start(Settings(oidc: false, redis: true)));

        Assert.NotNull(thrown);
        Assert.Contains("Oidc", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_refuses_to_start_with_no_redis_configured()
    {
        // §7's connect ticket and role cache both live in Redis. A host that
        // started without knowing where Redis is would fail at negotiate, per
        // user, at runtime — rather than here, once, at deploy.
        var thrown = Record.Exception(() => Start(Settings(oidc: true, redis: false)));

        Assert.NotNull(thrown);
        Assert.Contains("Redis", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_refuses_to_start_with_no_database_configured()
    {
        var thrown = Record.Exception(() => Start(Settings(oidc: true, redis: true, postgres: false)));

        Assert.NotNull(thrown);
        Assert.Contains("Postgres", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_starts_when_everything_is_configured()
    {
        // Without this, the tests above would pass just as well against a host
        // that never starts at all.
        Assert.Null(Record.Exception(() => Start(Settings(oidc: true, redis: true))));
    }

    private static Dictionary<string, string?> Settings(bool oidc, bool redis, bool postgres = true)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (oidc)
        {
            settings["Oidc:Issuer"] = ApiFactory.Issuer;
            settings["Oidc:Audience"] = ApiFactory.Audience;
            settings["Oidc:MetadataAddress"] = ApiFactory.Issuer + ".well-known/openid-configuration";
        }

        if (redis)
        {
            settings["Redis:Configuration"] = "127.0.0.1:1,abortConnect=false,connectTimeout=1,connectRetry=0";
        }

        if (postgres)
        {
            settings["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=editor;Username=editor;Timeout=1";
        }

        return settings;
    }

    private static void Start(Dictionary<string, string?> settings)
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(
                configuration => configuration.AddInMemoryCollection(settings)));

        // Creating a client is what builds and starts the host.
        using var client = factory.CreateClient();
    }

    private static string Flatten(Exception exception)
    {
        var text = exception.Message;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            text += "\n" + inner.Message;
        }

        return text;
    }
}
