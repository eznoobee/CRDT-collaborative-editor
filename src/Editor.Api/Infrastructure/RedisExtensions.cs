using Editor.Infrastructure.Tickets;
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

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            var parsed = ConfigurationOptions.Parse(options.Configuration);

            // Reconnect rather than fail permanently: see the remark above.
            parsed.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(parsed);
        });

        services.AddSingleton<IConnectTicketStore>(provider => new RedisConnectTicketStore(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            provider.GetRequiredService<IOptions<ConnectTicketOptions>>().Value));

        return services;
    }
}
