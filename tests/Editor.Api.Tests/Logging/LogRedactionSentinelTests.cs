using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Editor.Api.Tests.Logging;

/// <summary>
/// §7: a ticket or token must never be written to a log, proven with a known
/// sentinel driven through the real pipeline.
/// </summary>
/// <remarks>
/// §7 says why it is written this way: "no log line contains a token" is not
/// testable as stated, because a passing run proves only that this run's tokens
/// did not appear. A sentinel makes it testable — put a value nothing else
/// could produce into every place a credential travels, exercise the paths that
/// log, and assert the value is in no record.
/// <para>
/// The sentinel is deliberately not shaped like a real ticket. A 43-character
/// base64url value would be caught by the bare-ticket pattern regardless of
/// context, and the test would then prove that one pattern works rather than
/// that the query, header and exception paths are covered.
/// </para>
/// </remarks>
public sealed class LogRedactionSentinelTests
{
    private const string Sentinel = "SENTINEL-Ns7xQ7fL-DO-NOT-LOG-ME";

    private static (WebApplicationFactory<Program> Factory, CapturingLoggerProvider Sink) Host()
    {
        var sink = new CapturingLoggerProvider();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Oidc:Issuer"] = ApiFactory.Issuer,
                    ["Oidc:Audience"] = ApiFactory.Audience,
                    ["Oidc:MetadataAddress"] = ApiFactory.Issuer + ".well-known/openid-configuration",
                    ["Redis:Configuration"] = "127.0.0.1:1,abortConnect=false,connectTimeout=1,connectRetry=0",
                    ["Postgres:ConnectionString"] = "Host=127.0.0.1;Port=1;Database=editor;Username=editor;Timeout=1",

                    // appsettings.json holds Microsoft.AspNetCore at Warning,
                    // which suppresses the hosting layer's "Request starting"
                    // line — the one that carries the raw query string. Turning
                    // it back on is the point: a level that happens to hide a
                    // credential is not redaction, and the first thing anyone
                    // does while debugging an incident is raise the level.
                    ["Logging:LogLevel:Default"] = "Trace",
                    ["Logging:LogLevel:Microsoft"] = "Trace",
                    ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
                }));

            builder.ConfigureLogging(logging =>
            {
                logging.AddProvider(sink);

                // Everything, at every level. Redaction that only holds while
                // logging is turned down is not redaction — a deployment
                // raising the level to debug an incident is exactly when the
                // credential would leak.
                logging.SetMinimumLevel(LogLevel.Trace);
            });

        });

        return (factory, sink);
    }

    private static async Task DriveAsync(HttpClient client, string path, bool header)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"{path}?access_token={Sentinel}&doc=42", UriKind.Relative));

        if (header)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Sentinel);
        }

        try
        {
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            _ = response.StatusCode;
        }
        catch (Exception)
        {
            // The throwing path propagates through TestServer. The exception is
            // not what this test asserts on — the log records are.
        }
    }

    private static void ThrowThroughTheRealPipeline(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Editor.Api.Tests.Sentinel");

        var inner = new InvalidOperationException(
            $"inner detail quoting the header: Bearer {Sentinel}");
        var outer = new InvalidOperationException(
            $"failed handling https://editor.example/hub?access_token={Sentinel}&doc=42", inner);

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["Ticket"] = Sentinel,
            ["Authorization"] = $"Bearer {Sentinel}",
        }))
        {
            // Logged through ILogger.Log directly rather than the LogError
            // extension, so the structured state reaches the pipeline exactly
            // as a source-generated logger's would (and so CA1848 has nothing
            // to object to).
            logger.Log(
                LogLevel.Error,
                new EventId(1, "SentinelFailure"),
                new List<KeyValuePair<string, object?>>
                {
                    new("Url", $"https://editor.example/hub?access_token={Sentinel}"),
                    new("{OriginalFormat}", "Submitting to {Url} failed"),
                },
                outer,
                static (state, _) => $"Submitting to {state[0].Value} failed");
        }
    }

    [Fact]
    public async Task No_sink_receives_the_sentinel_on_any_path()
    {
        var (factory, sink) = Host();
        using (factory)
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            await DriveAsync(client, "/health/live", header: false);
            await DriveAsync(client, "/health/live", header: true);
            await DriveAsync(client, "/no-such-path", header: true);

            // The framework's own exception path is exercised above — an
            // unreachable issuer plus a Bearer header makes JwtBearerHandler
            // throw and log on every request — but none of the exceptions it
            // raises quote the token, so that alone would leave the exception
            // branch of the redactor unproven. This is the case that does
            // quote it: application code failing with the URL and the header in
            // the message, which is what a hub method's catch block looks like.
            // It goes through the host's own ILoggerFactory, so it is the same
            // pipeline, the same filters and the same providers as everything
            // above.
            ThrowThroughTheRealPipeline(factory.Services);
        }

        var records = sink.Records;

        // Guard against the test passing because nothing was logged at all.
        Assert.NotEmpty(records);
        Assert.Contains(records, record => record.Contains("access_token", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("exception|", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("|scope|", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("inner detail quoting the header", StringComparison.Ordinal));

        var leaked = records
            .Where(record => record.Contains(Sentinel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"§7 forbids a credential reaching any sink. {leaked.Count} record(s) carried the sentinel:\n  "
            + string.Join("\n  ", leaked.Take(10)));
    }

    [Fact]
    public async Task The_surrounding_log_line_survives_redaction()
    {
        // Redaction that dropped the record, or blanked the message, would pass
        // the test above and leave nobody able to debug anything. What has to
        // survive is everything except the credential.
        var (factory, sink) = Host();
        using (factory)
        {
            using var client = factory.CreateClient();
            await DriveAsync(client, "/health/live", header: true);
        }

        var records = sink.Records;

        Assert.Contains(records, record => record.Contains("/health/live", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("doc=42", StringComparison.Ordinal));
        Assert.Contains(records, record => record.Contains("[redacted]", StringComparison.Ordinal));
    }
}
