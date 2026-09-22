using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Editor.Api.Tests.Persistence;

/// <summary>
/// A real Postgres for the persistence tests (PROJECT_SPEC.md §11, Phase 2).
/// </summary>
/// <remarks>
/// Prefers a server named by <c>EDITOR_TEST_POSTGRES</c> when one is set, and
/// starts a container otherwise. The override exists so the schema can be
/// exercised on a machine without a Docker daemon; CI sets nothing and gets the
/// container, which is what the done-when requires.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>Why there is no database, when there is none.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("EDITOR_TEST_POSTGRES");

        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            try
            {
                _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
                await _container.StartAsync(TestContext.Current.CancellationToken);
                ConnectionString = _container.GetConnectionString();
            }
            catch (Exception ex)
            {
                // Same posture as the infrastructure smoke tests: a missing
                // daemon is a skip on a developer machine and a failure in CI,
                // where Phase 2's done-when requires these to run.
                if (string.Equals(
                        Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Postgres is required for the persistence tests in CI "
                        + "(PROJECT_SPEC.md §11, Phase 2). Set EDITOR_TEST_POSTGRES or provide Docker.",
                        ex);
                }

                SkipReason =
                    $"No Postgres: Docker is unavailable and EDITOR_TEST_POSTGRES is unset ({ex.Message})";
                return;
            }
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var context = CreateContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Skips the calling test when no database could be reached.</summary>
    public void RequireDatabase() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public EditorDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<EditorDbContext>()
            .UseNpgsql(ConnectionString)
            .Options);

    /// <summary>A document id unique to one test, so tests do not collide.</summary>
    public static Guid NewDocumentId() => Guid.NewGuid();
}

/// <summary>Shares one database across the persistence tests.</summary>
/// <remarks>
/// Named without the "Collection" suffix CA1711 objects to; xUnit only needs the
/// attribute, not the name.
/// </remarks>
[CollectionDefinition(nameof(PostgresTests))]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>;
