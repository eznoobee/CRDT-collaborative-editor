using Editor.Api.Tests.Tickets;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using StackExchange.Redis;

// StackExchange.Redis has a Role type of its own, for replication topology.
using Role = Editor.Domain.Role;

namespace Editor.Api.Tests.Authorization;

/// <summary>
/// The role cache's own behaviour, separate from the hub that uses it.
/// </summary>
[Collection(nameof(RedisTests))]
public sealed class DocumentRoleCacheTests
{
    private readonly RedisFixture _redis;

    public DocumentRoleCacheTests(RedisFixture redis) => _redis = redis;

    private sealed class FixedRoles : IDocumentRoles
    {
        public Task<Role?> GetRoleAsync(Guid documentId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<Role?>(Role.Editor);
    }

    [Fact]
    public async Task Subscribing_after_the_multiplexer_has_gone_does_not_throw()
    {
        // The multiplexer and the cache are both singletons and nothing orders
        // their disposal. On a slow shutdown the multiplexer goes first, a
        // ConnectionRestored event fires into a resubscribe, and SubscribeAsync
        // throws ObjectDisposedException — out of a hosted service, which fails
        // the host's shutdown and, in a test host, fails whichever test happened
        // to be running. CI caught it once on an intermediate commit and the
        // next run was green, so the only version of this test worth having is
        // one that forces the losing order.
        //
        // StartAsync is the same subscribe path the reconnect handler uses;
        // driving it against an already-disposed multiplexer reproduces the
        // throw deterministically.
        _redis.RequireRedis();

        var multiplexer = await ConnectionMultiplexer.ConnectAsync(_redis.Configuration);
        var cache = new CachedDocumentRoles(
            multiplexer,
            new FixedRoles(),
            new DocumentRoleCacheOptions { KeyPrefix = $"test:{Guid.NewGuid():N}:" },
            TimeProvider.System);

        await multiplexer.DisposeAsync();

        Assert.Null(await Record.ExceptionAsync(
            async () => await cache.StartAsync(TestContext.Current.CancellationToken)));
        Assert.Null(await Record.ExceptionAsync(async () => await cache.DisposeAsync()));
    }

    [Fact]
    public async Task An_unreachable_redis_does_not_stop_it_starting()
    {
        // §10: Redis being reachable is a readiness question, not a startup
        // one. A fleet that refuses to start while Redis restarts turns a brief
        // dependency outage into an outage, and the five-second TTL is the
        // correctness guarantee that holds without a single invalidation
        // message ever arriving.
        var unreachable = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { { "127.0.0.1", 1 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 50,
            ConnectRetry = 0,
        });

        var cache = new CachedDocumentRoles(
            unreachable, new FixedRoles(), new DocumentRoleCacheOptions(), TimeProvider.System);

        Assert.Null(await Record.ExceptionAsync(
            async () => await cache.StartAsync(TestContext.Current.CancellationToken)));

        await cache.DisposeAsync();
        await unreachable.DisposeAsync();
    }
}
