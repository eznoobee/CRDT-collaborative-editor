using System.Net;
using System.Text.Json;
using Editor.Api.Tests.Hubs;
using Npgsql;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's <c>/health/ready</c>, and register row 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vacuity risk, and it is the register row's own words:</b> an endpoint
/// that returns healthy without checking anything passes every test that only
/// runs against a working stack. So no test here asserts "healthy" alone; each
/// of the unhealthy cases takes a real dependency away and requires the
/// endpoint to name the one that went.
/// </para><para>
/// <b>The case that makes this more than a checkbox is
/// <see cref="Postgres_that_is_reachable_but_unmigrated_is_not_ready"/>.</b> A
/// probe that opens a connection and issues no query passes the two
/// dead-port tests below and still reports healthy against an empty database —
/// which is §13.28 exactly, the case where <c>/health/live</c> answered 200
/// from a process with no schema behind it. Reproducing that failure under a
/// better name is the thing worth guarding against, and only a reachable-but-
/// useless dependency can catch it.
/// </para><para>
/// <b>What these tests do not distinguish</b>, said plainly: pointing a factory
/// at a dead port proves the probe dials, and at the socket level "the server
/// stopped" and "the address is wrong" are the same signal. That is acceptable
/// because the probe's answer is the same in both cases. It is not acceptable
/// as a stand-in for the unmigrated case, which is why that one is separate.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ReadinessTests
{
    private static readonly Uri Ready = new("/health/ready", UriKind.Relative);
    private static readonly Uri Live = new("/health/live", UriKind.Relative);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EditorFixture _fixture;

    public ReadinessTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_stack_with_both_dependencies_up_is_ready()
    {
        // On its own this proves nothing — a hardcoded healthy passes it. It is
        // here as the other half of the pair: without it, "reports unhealthy"
        // is satisfied by an endpoint that is never ready, which would keep
        // every instance out of rotation forever.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Ready, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await ReadAsync(response);
        Assert.Equal("Healthy", report.Status);
        Assert.All(report.Checks, check => Assert.Equal("Healthy", check.Status));
    }

    [Fact]
    public async Task Postgres_that_is_not_there_makes_the_stack_unready_by_name()
    {
        _fixture.RequireBoth();

        await using var factory = new EditorApiFactory(_fixture, settings: new()
        {
            ["Postgres:ConnectionString"] = Unreachable(_fixture.Postgres.ConnectionString),
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Ready, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var report = await ReadAsync(response);
        var postgres = Assert.Single(report.Checks, check => check.Name == "postgres");

        Assert.Equal("Unhealthy", postgres.Status);

        // Named and explained. "Unhealthy" alone sends whoever is paged to read
        // the logs this endpoint could have handed them (§13.13).
        Assert.False(
            string.IsNullOrWhiteSpace(postgres.Detail),
            "readiness reported postgres unhealthy without saying why");

        // And Redis is still fine, so the report distinguishes them rather than
        // failing everything whenever anything fails.
        var redis = Assert.Single(report.Checks, check => check.Name == "redis");
        Assert.Equal("Healthy", redis.Status);
    }

    [Fact]
    public async Task Redis_that_is_not_there_makes_the_stack_unready_by_name()
    {
        _fixture.RequireBoth();

        await using var factory = new EditorApiFactory(_fixture, settings: new()
        {
            // Derived from the fixture's own configuration rather than
            // written out. The first version of this test hardcoded
            // "localhost:6399" as an obviously dead port — which is the port
            // the fixture's Redis actually listens on, so the test pointed at a
            // working server and reported healthy. Deriving the host and
            // changing only the port cannot make that mistake.
            ["Redis:Configuration"] = Silent(_fixture.Redis.Configuration),
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Ready, TestContext.Current.CancellationToken);

        var report = await ReadAsync(response);
        var redis = Assert.Single(report.Checks, check => check.Name == "redis");

        Assert.Equal("Unhealthy", redis.Status);
        Assert.False(
            string.IsNullOrWhiteSpace(redis.Detail),
            "readiness reported redis unhealthy without saying why");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Postgres_that_is_reachable_but_unmigrated_is_not_ready()
    {
        // THE ONE THAT MATTERS. §13.28: a stack that comes up against an empty
        // schema satisfies a liveness probe completely, and it satisfies a
        // readiness probe too if that probe only opens a connection. Postgres
        // here is up, reachable, and answering — and has none of this
        // application's tables in it.
        _fixture.RequireBoth();

        var empty = await EmptyDatabaseAsync();
        if (empty is null)
        {
            Assert.Skip("could not create an empty database to probe against");
        }

        try
        {
            await using var factory = new EditorApiFactory(_fixture, settings: new()
            {
                ["Postgres:ConnectionString"] = empty,
            });

            using var client = factory.CreateClient();
            using var response = await client.GetAsync(Ready, TestContext.Current.CancellationToken);

            var report = await ReadAsync(response);
            var postgres = Assert.Single(report.Checks, check => check.Name == "postgres");

            Assert.Equal("Unhealthy", postgres.Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await DropAsync(empty);
        }
    }

    [Fact]
    public async Task Liveness_stays_up_while_readiness_is_down()
    {
        // They answer different questions and an orchestrator does different
        // things with them. Liveness failing gets the process restarted, so a
        // liveness probe that touched Redis would roll the whole fleet over a
        // dependency outage the application is built to ride out — which is
        // §10's own reason for AbortOnConnectFail = false.
        _fixture.RequireBoth();

        await using var factory = new EditorApiFactory(_fixture, settings: new()
        {
            ["Postgres:ConnectionString"] = Unreachable(_fixture.Postgres.ConnectionString),
        });

        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(Ready, TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(Live, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    /// <summary>The same Redis host, on a port nothing serves.</summary>
    /// <remarks>
    /// Port 9 is discard: assigned, and nothing in this environment binds it.
    /// The abort-on-fail behaviour the application configures (§10) is left
    /// exactly as it ships, because a probe that only works against a
    /// differently configured multiplexer answers for a client nobody uses.
    /// </remarks>
    private static string Silent(string configuration)
    {
        var host = configuration.Split(':')[0];
        return $"{host}:9,abortConnect=false,connectTimeout=2000,syncTimeout=2000";
    }

    /// <summary>The same connection string, pointed at a port nothing serves.</summary>
    private static string Unreachable(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Port = 5, // assigned to an unused protocol; nothing listens here
            Timeout = 2,
            CommandTimeout = 2,
        }.ConnectionString;

    /// <summary>A real database with none of this application's tables in it.</summary>
    private static async Task<string?> EmptyDatabaseAsync()
    {
        var name = $"ready_probe_{Guid.NewGuid():N}";
        var builder = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("EDITOR_TEST_POSTGRES") ?? string.Empty);

        if (string.IsNullOrWhiteSpace(builder.Host))
        {
            return null;
        }

        var administrative = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = "postgres",
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(administrative);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", connection))
        {
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Database = name,
        }.ConnectionString;
    }

    private static async Task DropAsync(string? connectionString)
    {
        if (connectionString is null)
        {
            return;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var name = builder.Database;
        builder.Database = "postgres";

        NpgsqlConnection.ClearAllPools();

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE);", connection);

        await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<Report> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var report = JsonSerializer.Deserialize<Report>(body, Json);

        Assert.NotNull(report);
        return report;
    }

    private sealed record Report(string Status, IReadOnlyList<Check> Checks);

    private sealed record Check(string Name, string Status, string? Detail);
}
