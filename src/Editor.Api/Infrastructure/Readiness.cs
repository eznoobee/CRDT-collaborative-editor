using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using StackExchange.Redis;

namespace Editor.Api.Infrastructure;

/// <summary>§10's readiness probe: Postgres and Redis, reported by name.</summary>
/// <remarks>
/// <para>
/// <strong>Readiness reads a row; it does not dial a socket.</strong> Postgres
/// accepts connections against a database with no schema in it, which is
/// §13.28's case exactly — a stack that came up against an empty database and
/// satisfied `/health/live` completely. A probe that only opened a connection
/// would reproduce that failure under a better name, so this one issues the
/// narrowest query that would break if the deployment were wrong.
/// </para><para>
/// <strong>It uses the application's own connections, never its own.</strong>
/// §13.31: a probe with its own connection settings can be healthy while the
/// path the application actually takes is broken, and the endpoint's whole
/// value is that it answers for the real one.
/// </para>
/// </remarks>
public static class Readiness
{
    /// <summary>What readiness calls each dependency when it reports on it.</summary>
    public const string Postgres = "postgres";

    /// <summary>What readiness calls Redis when it reports on it.</summary>
    public const string Redis = "redis";

    /// <summary>The tag that separates readiness from liveness.</summary>
    public const string Ready = "ready";

    public static IServiceCollection AddEditorReadiness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks()
            .AddCheck<PostgresReadiness>(Postgres, tags: [Ready])
            .AddCheck<RedisReadiness>(Redis, tags: [Ready]);

        return services;
    }

    /// <summary>
    /// Writes which dependency failed and why (§10, §13.13).
    /// </summary>
    /// <remarks>
    /// The default writer emits the aggregate status and nothing else, so a
    /// probe built on it can only ever say "unhealthy" — which sends whoever is
    /// paged to read the logs they could have been handed here. §13.13's rule
    /// generalises past rejections: a signal the reader cannot act on is not a
    /// signal. This is the smallest body that answers "which one, and why".
    /// </remarks>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json";

        var entries = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            detail = entry.Value.Description,
        });

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = entries,
        });
    }
}

/// <summary>Postgres is reachable <em>and</em> the schema is there.</summary>
internal sealed class PostgresReadiness : IHealthCheck
{
    // The narrowest read that fails against an unmigrated database. Counting
    // rows would be slower and prove nothing extra; selecting a constant would
    // prove only that a connection opened.
    private const string Probe = "SELECT 1 FROM documents LIMIT 1;";

    private readonly NpgsqlDataSource _dataSource;

    public PostgresReadiness(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = _dataSource.CreateCommand(Probe);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("postgres answered a query");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception's message, not a generic one: "which dependency and
            // why" is the whole operational value of this endpoint, and an
            // answer of "unhealthy" sends whoever is paged to read logs they
            // could have been given here.
            return HealthCheckResult.Unhealthy(
                $"postgres did not answer a query: {exception.Message}", exception);
        }
    }
}

/// <summary>Redis is reachable and answering commands.</summary>
internal sealed class RedisReadiness : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisReadiness(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // PING rather than IsConnected. The multiplexer is configured with
            // AbortOnConnectFail false so that a Redis restart does not make it
            // fail permanently (§10), which means it reports a state rather
            // than attempting anything — and a state can be stale.
            await _redis.GetDatabase().PingAsync().ConfigureAwait(false);
            return HealthCheckResult.Healthy("redis answered a ping");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(
                $"redis did not answer a ping: {exception.Message}", exception);
        }
    }
}
