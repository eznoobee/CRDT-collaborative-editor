using Editor.Domain;
using Editor.Infrastructure.Tickets;
using StackExchange.Redis;

// StackExchange.Redis has a Role type of its own, for replication topology.
using Role = Editor.Domain.Role;

namespace Editor.Api.Tests.Tickets;

/// <summary>
/// §7's connect ticket: opaque, single-use, ≤60 seconds, redeemed atomically.
/// </summary>
[Collection(nameof(RedisTests))]
public sealed class ConnectTicketTests
{
    private readonly RedisFixture _redis;

    public ConnectTicketTests(RedisFixture redis) => _redis = redis;

    private static ConnectionBinding Binding(Role role = Role.Editor) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), role, Guid.NewGuid());

    private RedisConnectTicketStore Store(TimeSpan? lifetime = null) =>
        new(_redis.Redis, new ConnectTicketOptions
        {
            Lifetime = lifetime ?? TimeSpan.FromSeconds(60),
            // One prefix per test run, so a rerun against a long-lived Redis
            // cannot redeem a ticket a previous run left behind.
            KeyPrefix = $"test:{Guid.NewGuid():N}:",
        });

    [Fact]
    public async Task A_ticket_carries_the_binding_the_server_chose()
    {
        _redis.RequireRedis();
        var store = Store();
        var binding = Binding(Role.Owner);

        var ticket = await store.IssueAsync(binding, TestContext.Current.CancellationToken);
        var redeemed = await store.RedeemAsync(ticket, TestContext.Current.CancellationToken);

        Assert.Equal(binding, redeemed);
    }

    [Fact]
    public async Task Redeeming_twice_fails_the_second_time()
    {
        _redis.RequireRedis();
        var store = Store();

        var ticket = await store.IssueAsync(Binding(), TestContext.Current.CancellationToken);

        Assert.NotNull(await store.RedeemAsync(ticket, TestContext.Current.CancellationToken));
        Assert.Null(await store.RedeemAsync(ticket, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Simultaneous_redemptions_of_one_ticket_yield_exactly_one_success()
    {
        // This is the test §7's GETDEL rule exists for. A read-then-delete
        // passes every test above: two connects arriving together both observe
        // the ticket present, both proceed, and single-use is a comment rather
        // than a property. One ticket redeemed by one racer is not evidence —
        // the race has to actually be run, many times, or the implementation
        // gets to be wrong on a schedule nobody controls.
        _redis.RequireRedis();
        var store = Store();
        const int Tickets = 150;
        const int RacersPerTicket = 8;

        var bindings = new Dictionary<string, ConnectionBinding>(StringComparer.Ordinal);
        foreach (var _ in Enumerable.Range(0, Tickets))
        {
            var binding = Binding();
            bindings[await store.IssueAsync(binding, TestContext.Current.CancellationToken)] = binding;
        }

        var multipleWinners = new List<string>();
        var noWinner = new List<string>();
        var wrongBinding = new List<string>();

        foreach (var (ticket, expected) in bindings)
        {
            // A barrier, so the racers contend rather than queue behind each
            // other's setup cost.
            using var start = new SemaphoreSlim(0, RacersPerTicket);
            var racers = Enumerable.Range(0, RacersPerTicket).Select(async _ =>
            {
                await start.WaitAsync(TestContext.Current.CancellationToken);
                return await store.RedeemAsync(ticket, TestContext.Current.CancellationToken);
            }).ToList();

            start.Release(RacersPerTicket);
            var outcomes = await Task.WhenAll(racers);

            var winners = outcomes.Where(outcome => outcome is not null).ToList();
            switch (winners.Count)
            {
                case 0:
                    noWinner.Add(ticket);
                    break;
                case 1:
                    if (winners[0] != expected)
                    {
                        wrongBinding.Add(ticket);
                    }

                    break;
                default:
                    multipleWinners.Add(ticket);
                    break;
            }
        }

        Assert.True(
            multipleWinners.Count == 0,
            $"{multipleWinners.Count} of {Tickets} tickets were redeemed more than once. "
            + "Redemption is not atomic; §7 requires GETDEL, not a read then a delete.");
        Assert.Empty(noWinner);
        Assert.Empty(wrongBinding);
    }

    [Fact]
    public async Task An_expired_ticket_is_not_redeemable()
    {
        _redis.RequireRedis();
        var store = Store(TimeSpan.FromMilliseconds(200));

        var ticket = await store.IssueAsync(Binding(), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Null(await store.RedeemAsync(ticket, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_ticket_lifetime_is_the_configured_one_and_it_is_short()
    {
        // Asserting the TTL directly rather than by waiting: a test that waits
        // 60 seconds does not get written, and one that waits a shorter time
        // proves only that a shorter TTL was set somewhere.
        _redis.RequireRedis();
        var prefix = $"test:{Guid.NewGuid():N}:";
        var store = new RedisConnectTicketStore(
            _redis.Redis, new ConnectTicketOptions { Lifetime = TimeSpan.FromSeconds(30), KeyPrefix = prefix });

        var ticket = await store.IssueAsync(Binding(), TestContext.Current.CancellationToken);
        var ttl = await _redis.Redis.GetDatabase().KeyTimeToLiveAsync(prefix + ticket);

        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61)]
    [InlineData(300)]
    public void A_lifetime_outside_section_7s_bound_is_refused_at_construction(int seconds)
    {
        // The options class carries the same [Range], and that validation is
        // skippable by anyone constructing the store directly. §7's ceiling has
        // to hold at the point where the TTL is actually chosen.
        var options = new ConnectTicketOptions { Lifetime = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RedisConnectTicketStore(NullMultiplexer(), options));
    }

    [Fact]
    public async Task A_bearer_token_presented_as_a_ticket_is_refused()
    {
        // §7 exists because the two are different credentials with different
        // lifetimes. A store that shrugged and looked up a JWT would make the
        // ticket optional, and the query parameter would be carrying a bearer
        // token into every proxy log again.
        _redis.RequireRedis();
        var store = Store();
        const string Jwt =
            "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9"
            + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwiYXVkIjoiZWRpdG9yLWFwaSJ9"
            + ".c2lnbmF0dXJlLXdoaWNoLWlzLW5vdC12YWxpZGF0ZWQtaGVyZQ";

        Assert.Null(await store.RedeemAsync(Jwt, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("connect-ticket:*")]
    [InlineData("../connect-ticket:abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/")]
    public async Task A_value_that_is_not_ticket_shaped_never_reaches_redis(string candidate)
    {
        _redis.RequireRedis();
        var store = Store();

        Assert.Null(await store.RedeemAsync(candidate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tickets_are_opaque_and_unrelated_to_what_they_bind()
    {
        // "Opaque" in §7 is not decoration: a ticket derived from the ids it
        // carries lets whoever sees one in a proxy log learn the document and
        // the user, and lets them forge the next one.
        _redis.RequireRedis();
        var store = Store();
        var binding = Binding();

        var first = await store.IssueAsync(binding, TestContext.Current.CancellationToken);
        var second = await store.IssueAsync(binding, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);

        foreach (var id in new[] { binding.UserId, binding.DocumentId, binding.ReplicaId })
        {
            Assert.DoesNotContain(id.ToString("N"), first, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                Convert.ToHexString(id.ToByteArray(bigEndian: true)), first, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_value_stored_under_a_ticket_key_by_something_else_is_not_a_binding()
    {
        // Redis is shared infrastructure. A key in this namespace holding
        // anything but a 49-byte payload is not a ticket, and decoding whatever
        // was there would hand a connection a binding assembled from someone
        // else's bytes.
        _redis.RequireRedis();
        var prefix = $"test:{Guid.NewGuid():N}:";
        var store = new RedisConnectTicketStore(
            _redis.Redis, new ConnectTicketOptions { KeyPrefix = prefix });

        var forged = new string('A', 43);
        await _redis.Redis.GetDatabase().StringSetAsync(prefix + forged, "not a binding");

        Assert.Null(await store.RedeemAsync(forged, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A multiplexer for the tests that must not reach Redis to fail.
    /// </summary>
    private static ConnectionMultiplexer NullMultiplexer() =>
        ConnectionMultiplexer.Connect(new ConfigurationOptions
        {
            EndPoints = { { "127.0.0.1", 1 } },
            AbortOnConnectFail = false,
            ConnectTimeout = 1,
            ConnectRetry = 0,
        });
}
