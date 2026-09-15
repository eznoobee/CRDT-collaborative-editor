using Editor.Api.Tests.Persistence;
using Editor.Api.Tests.Tickets;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Real Postgres and real Redis, for the tests that need both.
/// </summary>
/// <remarks>
/// §7's authorization is a property of a database read, a Redis cache, a
/// pub/sub channel and a TTL working together. Faking any of them would test
/// the fake.
/// </remarks>
public sealed class EditorFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();

    public RedisFixture Redis { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await Redis.InitializeAsync();
    }

    /// <summary>Skips the calling test when either dependency is missing.</summary>
    public void RequireBoth()
    {
        Postgres.RequireDatabase();
        Redis.RequireRedis();
    }

    public async ValueTask DisposeAsync()
    {
        await Redis.DisposeAsync();
        await Postgres.DisposeAsync();
    }
}

/// <summary>Shares one Postgres and one Redis across the hub tests.</summary>
[CollectionDefinition(nameof(EditorTests))]
public sealed class EditorTests : ICollectionFixture<EditorFixture>;
