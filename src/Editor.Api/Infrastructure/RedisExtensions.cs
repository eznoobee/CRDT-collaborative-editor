using Editor.Infrastructure.Tickets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Editor.Api.Infrastructure;

/// <summary>Redis, and the things §7 keeps in it.</summary>
public static class RedisExtensions
{
    /// <summary>
    /// Registers the shared Redis connection and the connect-ticket store.
    /// </summary>
    /// <remarks>
    /// The multiplexer is created on first use rather than at startup. Redis
    /// being reachable is a readiness question (§10), not a startup one: an app
    /// server that refuses to start because Redis is restarting turns a brief
    /// dependency outage into a fleet that is down. Configuration being present
    /// is the startup question, and that is what ValidateOnStart covers.
    /// </remarks>
    public static IServiceCollection AddEditorRedis(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection(RedisOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ConnectTicketOptions>()
            .Bind(configuration.GetSection(ConnectTicketOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(provider => ConnectionMultiplexer.Connect(
            Parse(provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration)));

        services.AddSingleton<IConnectTicketStore>(provider => new RedisConnectTicketStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<IOptions<ConnectTicketOptions>>().Value));

        // Off, and post-configured so nothing can turn it back on. With
        // detailed errors on, every hub failure sends the exception's type and
        // message to whoever is connected — server internals to any client —
        // and §7's error codes stop being the whole of what the client sees,
        // which is what the 404-not-403 rule depends on.
        services.PostConfigure<HubOptions>(options => options.EnableDetailedErrors = false);

        // §8 forbids sticky sessions, so a client's operations and the
        // broadcasts it should receive routinely land on different instances.
        // The backplane is what makes that work; without it a document is only
        // collaborative among the clients that happened to hit one server.
        services.AddSignalR()
            .AddStackExchangeRedis(options => options.ConnectionFactory = writer =>
                // Its own connection rather than the application's: the
                // backplane subscribes and publishes continuously, and sharing
                // a multiplexer means one slow consumer stalls ticket
                // redemption too.
                ConnectionMultiplexer.ConnectAsync(Parse(Configured(configuration)), writer)
                    .ContinueWith(
                        task => (IConnectionMultiplexer)task.GetAwaiter().GetResult(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));

        return services;
    }

    private static string Configured(IConfiguration configuration) =>
        configuration.GetSection(RedisOptions.Section)["Configuration"]
        ?? throw new InvalidOperationException(
            $"'{RedisOptions.Section}:Configuration' is missing. There is no default.");

    /// <summary>
    /// Redis configuration that reconnects rather than failing permanently.
    /// </summary>
    /// <remarks>
    /// AbortOnConnectFail defaults to true, which turns a Redis restart into a
    /// multiplexer that never recovers — the process keeps serving and every
    /// ticket redemption fails until someone restarts it. Reachability is a
    /// readiness question (§10), not a fatal one.
    /// </remarks>
    private static ConfigurationOptions Parse(string value)
    {
        var parsed = ConfigurationOptions.Parse(value);
        parsed.AbortOnConnectFail = false;
        return parsed;
    }
}
