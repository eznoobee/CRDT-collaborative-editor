using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Editor.Api.Logging;

/// <summary>Puts credential redaction in front of every log provider.</summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Replaces the registered <see cref="ILoggerFactory"/> with one that
    /// redacts credentials (PROJECT_SPEC.md §7).
    /// </summary>
    /// <remarks>
    /// Decorating the existing registration rather than replacing it outright:
    /// the framework's factory keeps building loggers, filters and scopes
    /// exactly as before, and this adds one step in front. Constructing a
    /// LoggerFactory here instead would fork the framework's own wiring and
    /// drift from it silently.
    /// </remarks>
    public static IServiceCollection AddSecretRedaction(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = services.LastOrDefault(service => service.ServiceType == typeof(ILoggerFactory))
            ?? throw new InvalidOperationException(
                "No ILoggerFactory is registered. AddSecretRedaction must run after logging is configured, "
                + "or §7's redaction would silently cover nothing.");

        services.Remove(existing);
        services.TryAddSingleton<ILoggerFactory>(provider =>
            new RedactingLoggerFactory(Build(existing, provider)));

        return services;
    }

    private static ILoggerFactory Build(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        if (descriptor.ImplementationInstance is ILoggerFactory instance)
        {
            return instance;
        }

        if (descriptor.ImplementationFactory is not null)
        {
            return (ILoggerFactory)descriptor.ImplementationFactory(provider);
        }

        if (descriptor.ImplementationType is not null)
        {
            return (ILoggerFactory)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);
        }

        throw new InvalidOperationException("The registered ILoggerFactory cannot be constructed.");
    }
}
