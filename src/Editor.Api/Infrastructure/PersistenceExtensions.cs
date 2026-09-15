using System.ComponentModel.DataAnnotations;
using Editor.Api.Documents;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Editor.Api.Infrastructure;

/// <summary>Where the documents live.</summary>
public sealed class PostgresOptions
{
    public const string Section = "Postgres";

    /// <summary>
    /// An Npgsql connection string. No default, for the reason §7 gives about
    /// issuers and this project applies to every dependency: a fallback turns a
    /// misconfigured deployment into one that starts.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>Postgres, and the authorization that reads from it.</summary>
public static class PersistenceExtensions
{
    /// <summary>Registers the database and §7's two-tier role lookup.</summary>
    public static IServiceCollection AddEditorPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PostgresOptions>()
            .Bind(configuration.GetSection(PostgresOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DocumentRoleCacheOptions>()
            .Bind(configuration.GetSection(DocumentRoleCacheOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<EditorDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<IOptions<PostgresOptions>>().Value.ConnectionString));

        services.AddOptions<IngestLimits>()
            .Bind(configuration.GetSection(IngestLimits.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Npgsql directly for the hot path, EF for the schema and the rest (§3).
        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IOptions<PostgresOptions>>().Value.ConnectionString));

        services.AddSingleton<DocumentIngestState>();
        services.AddSingleton(provider => new IngestValidator(
            provider.GetRequiredService<DocumentIngestState>(),
            provider.GetRequiredService<IOptions<IngestLimits>>().Value));

        services.AddOptions<CatchUpOptions>()
            .Bind(configuration.GetSection(CatchUpOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider => new DocumentStore(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new CatchUpReader(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<DocumentStore>(),
            provider.GetRequiredService<IOptions<CatchUpOptions>>().Value));

        services.AddSingleton(provider => new OperationLogWriter(
            provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(provider => new OperationLogBatcher(
            provider.GetRequiredService<OperationLogWriter>(), BatchingPolicy.Default));

        services.AddScoped<CurrentUser>();
        services.AddScoped<DocumentRoleReader>();

        // The cache is a singleton holding process-wide state and a Redis
        // subscription; the reader behind it is scoped to a request, so it is
        // resolved per lookup from a scope of its own rather than captured.
        services.AddSingleton(provider => new CachedDocumentRoles(
            provider.GetRequiredService<IConnectionMultiplexer>(),
            new ScopedDocumentRoleReader(provider.GetRequiredService<IServiceScopeFactory>()),
            provider.GetRequiredService<IOptions<DocumentRoleCacheOptions>>().Value,
            provider.GetRequiredService<TimeProvider>()));

        services.AddSingleton<IDocumentRoles>(
            provider => provider.GetRequiredService<CachedDocumentRoles>());

        services.AddScoped<IDocumentRoleWriter>(provider => new InvalidatingDocumentRoleWriter(
            provider.GetRequiredService<DocumentRoleReader>(),
            provider.GetRequiredService<CachedDocumentRoles>()));

        services.AddHostedService<DocumentRoleCacheSubscriber>();

        return services;
    }

    /// <summary>
    /// Reads a role on a scope of its own, so a singleton cache can sit in
    /// front of a scoped DbContext without capturing one.
    /// </summary>
    private sealed class ScopedDocumentRoleReader : IDocumentRoles
    {
        private readonly IServiceScopeFactory _scopes;

        public ScopedDocumentRoleReader(IServiceScopeFactory scopes) => _scopes = scopes;

        public async Task<Domain.Role?> GetRoleAsync(
            Guid documentId, Guid userId, CancellationToken cancellationToken)
        {
            await using var scope = _scopes.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<DocumentRoleReader>()
                .GetRoleAsync(documentId, userId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Starts the cache's invalidation subscription with the host.</summary>
    private sealed class DocumentRoleCacheSubscriber : IHostedService
    {
        private readonly CachedDocumentRoles _cache;

        public DocumentRoleCacheSubscriber(CachedDocumentRoles cache) => _cache = cache;

        public Task StartAsync(CancellationToken cancellationToken) =>
            _cache.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) =>
            _cache.DisposeAsync().AsTask();
    }
}
