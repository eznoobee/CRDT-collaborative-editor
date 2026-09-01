using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Editor.Api.Tests;

/// <summary>
/// Proves the container-based integration test path works before Phase 2
/// depends on it. If Docker-in-CI is broken, this is where it should surface.
/// </summary>
public sealed class InfrastructureSmokeTests
{
    private const string PostgresImage = "postgres:16-alpine";
    private const string RedisImage = "redis:7-alpine";

    /// <summary>
    /// Requires Docker, or explains its absence.
    /// </summary>
    /// <remarks>
    /// Docker is mandatory in CI and optional on a developer machine. Skipping
    /// when the daemon is missing keeps local runs usable, but skipping in CI
    /// would quietly void done-when (d) of Phase 0 in PROJECT_SPEC.md §11. So a
    /// missing daemon is a skip locally and a failure in CI.
    /// </remarks>
    private static void RequireDocker()
    {
        try
        {
            // Building a container resolves and probes the Docker endpoint, so
            // this throws here rather than at StartAsync when there is no daemon.
            _ = new PostgreSqlBuilder(PostgresImage).Build();
        }
        catch (Exception ex)
        {
            var inCi = string.Equals(
                Environment.GetEnvironmentVariable("CI"),
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (inCi)
            {
                throw new InvalidOperationException(
                    "Docker is required for integration tests in CI "
                    + "(PROJECT_SPEC.md §11, Phase 0 done-when (d)).",
                    ex);
            }

            Assert.Skip($"Docker is not available on this machine: {ex.Message}");
        }
    }

    [Fact]
    public async Task Postgres_container_starts_and_accepts_a_connection()
    {
        RequireDocker();

        await using var postgres = new PostgreSqlBuilder(PostgresImage).Build();
        await postgres.StartAsync(TestContext.Current.CancellationToken);

        var result = await postgres.ExecScriptAsync(
            "SELECT 1;", TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Redis_container_starts_and_responds_to_ping()
    {
        RequireDocker();

        await using var redis = new RedisBuilder(RedisImage).Build();
        await redis.StartAsync(TestContext.Current.CancellationToken);

        var result = await redis.ExecAsync(
            ["redis-cli", "PING"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PONG", result.Stdout, StringComparison.Ordinal);
    }
}
