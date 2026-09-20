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

        // Uncached, and the interface says why: a listing is an enumeration
        // rather than an authorization decision, and a stale one would be wrong
        // in the direction that shows a revoked user what they can no longer
        // reach.
        services.AddScoped<IDocumentMemberships>(
            provider => provider.GetRequiredService<DocumentRoleReader>());

        // §9's removal. Scoped like the writer, and for the same reason: it
        // writes Postgres through the request's context and then invalidates.
        services.AddScoped<IDocumentRemoval>(provider => new DocumentRemoval(
            provider.GetRequiredService<EditorDbContext>(),
            provider.GetRequiredService<IDocumentMemberships>(),
            provider.GetRequiredService<CachedDocumentRoles>(),
            provider.GetRequiredService<TimeProvider>()));

        services.AddScoped<IDocumentRoleWriter>(provider => new InvalidatingDocumentRoleWriter(
            provider.GetRequiredService<DocumentRoleReader>(),
            provider.GetRequiredService<CachedDocumentRoles>()));

        // §5's stability frontier. Scoped, because it reads and writes through
        // the request-scoped DbContext like everything else that touches
        // Postgres; the hub takes a scope per acknowledgement rather than
        // holding one for the life of a connection.
        services.AddScoped<IStabilityFrontier, StabilityFrontier>();

        services.AddHostedService<DocumentRoleCacheSubscriber>();

        // §5's replica retirement, and the heartbeat without which it retires
        // live readers. Both are here rather than beside the Redis services
        // because both write Postgres; the retirement threshold and the
        // heartbeat interval share one options object so the relationship
        // between them is visible in one place.
        services.AddOptions<ReplicaRetirementOptions>()
            .BindConfiguration(ReplicaRetirementOptions.Section)
            .Validate(
                options => options.Heartbeat < options.Retire,
                "A heartbeat slower than T_retire cannot keep a live replica out of retirement.")
            .ValidateOnStart();

        services.AddSingleton<ReplicaRetirement>();
        services.AddHostedService(provider => provider.GetRequiredService<ReplicaRetirement>());

        // §10's instruments. A singleton because a Meter is one per process and
        // the instruments are its children; IMeterFactory is what the hosting
        // layer provides for exactly this.
        services.AddMetrics();

        // §10's state-derived readings, before the metrics that report them:
        // row 8 found that a counter beside a write is not a measurement of the
        // write, and these are counted from the rows instead.
        services.AddOptions<StateReadingOptions>()
            .BindConfiguration(StateReadingOptions.Section)
            .Validate(
                options => options.Interval > TimeSpan.Zero,
                "A state-reading interval of zero would query on every tick forever.")
            .ValidateOnStart();

        services.AddSingleton<StateReadings>();
        services.AddSingleton<IStateReadings>(provider => provider.GetRequiredService<StateReadings>());
        services.AddHostedService(provider => provider.GetRequiredService<StateReadings>());

        services.AddSingleton<EditorMetrics>();
        services.AddSingleton<Editor.Infrastructure.Observability.InfrastructureMetrics>();

        services.AddSingleton<ReplicaHeartbeat>();
        services.AddHostedService(provider => provider.GetRequiredService<ReplicaHeartbeat>());

        // §5's tombstone collection. Scoped like everything else that reads
        // Postgres through EF; the hosted service takes a scope per sweep.
        services.AddScoped<ISnapshotGarbageCollector, SnapshotGarbageCollector>();

        services.AddOptions<TombstoneCollectionOptions>()
            .BindConfiguration(TombstoneCollectionOptions.Section)
            .Validate(
                options => options.BatchSize > 0,
                "A collector with an empty batch examines no documents and reports success.")
            .ValidateOnStart();

        services.AddSingleton<TombstoneCollector>();
        services.AddHostedService(provider => provider.GetRequiredService<TombstoneCollector>());

        // Row 30: collection's second half. Collection shrinks a snapshot,
        // which is a cache of the replay; nothing is actually reclaimed until
        // the rows go, and this is what removes them.
        services.AddScoped<ILogTruncator, LogTruncator>();

        services.AddOptions<LogTruncationOptions>()
            .BindConfiguration(LogTruncationOptions.Section)
            .Validate(
                options => options.BatchSize > 0,
                "A truncation sweep with an empty batch examines no documents and reports success.")
            .Validate(
                options => options.Interval > TimeSpan.Zero,
                "A truncation sweep with a non-positive interval spins.")
            .ValidateOnStart();

        services.AddSingleton<LogTruncationSweeper>();
        services.AddHostedService(provider => provider.GetRequiredService<LogTruncationSweeper>());

        // §6's periodic snapshot. Register row 28: the policy and the writer
        // were built and nothing called them, so these three lines are the
        // subsystem — everything below them already existed.
        services.AddOptions<SnapshotOptions>()
            .BindConfiguration(SnapshotOptions.Section)
            .Validate(
                options => options.BatchSize > 0,
                "A snapshot sweep with an empty batch examines no documents and reports success.")
            .Validate(
                options => options.OperationsPerSnapshot >= 0,
                "A negative snapshot interval is due on every document forever.")
            .ValidateOnStart();

        // Registered by Type because SnapshotPolicy is a record struct and the
        // generic overload wants a reference type. Kept a struct deliberately:
        // §6's interval is a number with a rule attached, and the rule is the
        // only reason it is not an int.
        services.AddSingleton(
            typeof(SnapshotPolicy),
            provider => new SnapshotPolicy(
                provider.GetRequiredService<IOptions<SnapshotOptions>>().Value.OperationsPerSnapshot));

        services.AddScoped<IPeriodicSnapshotter, PeriodicSnapshotter>();

        services.AddSingleton<SnapshotSweeper>();
        services.AddSingleton<ISnapshotAge>(provider => provider.GetRequiredService<SnapshotSweeper>());
        services.AddHostedService(provider => provider.GetRequiredService<SnapshotSweeper>());

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
