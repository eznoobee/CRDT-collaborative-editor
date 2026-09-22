using Editor.Infrastructure.Tickets;

namespace Editor.Api.Tests.Tickets;

/// <summary>
/// The claim that stops two sessions authoring as one replica (§7).
/// </summary>
/// <remarks>
/// §5's tie-break assumes one author per replica id. Two live authors sharing
/// one can mint two different operations carrying the same <c>ElementId</c>, and
/// peers converge on whichever they saw first — differently. That is divergence
/// no test of either peer would catch, so the exclusion has to hold here.
/// <para>
/// The vacuity risk named before these were written: every assertion below is
/// about a *second* caller being refused, and a claim store that refused
/// everyone would satisfy all of them. So each refusal is paired with the
/// acquisition that must still succeed — after a release, after an expiry, for a
/// different replica — and the pairs are what make the refusals mean anything.
/// </para>
/// </remarks>
[Collection(nameof(RedisTests))]
public sealed class ReplicaClaimTests
{
    private readonly RedisFixture _redis;

    public ReplicaClaimTests(RedisFixture redis) => _redis = redis;

    private RedisReplicaClaims Claims(TimeSpan? lifetime = null)
    {
        var life = lifetime ?? TimeSpan.FromMinutes(2);
        return new RedisReplicaClaims(_redis.Redis, new ReplicaClaimOptions
        {
            Lifetime = life,

            // Derived, not fixed. A constant here was longer than the short
            // lifetime the expiry test uses, so the store's own guard threw at
            // construction and the test failed without ever reaching Redis.
            RefreshInterval = life / 4,
        });
    }

    [Fact]
    public async Task One_session_holds_a_replica_and_the_next_does_not()
    {
        _redis.RequireRedis();
        var claims = Claims();
        var document = Guid.CreateVersion7();
        var replica = Guid.CreateVersion7();

        var first = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        var second = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);

        // The pair that stops "refuses everyone" from passing: a different
        // replica on the same document is free.
        var other = await claims.TryClaimAsync(
            document, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task A_released_replica_can_be_claimed_again()
    {
        // The reason release exists rather than leaving it to the TTL: an owner
        // closing a tab and reopening it should resume immediately, not wait out
        // two minutes of a claim nobody holds.
        _redis.RequireRedis();
        var claims = Claims();
        var document = Guid.CreateVersion7();
        var replica = Guid.CreateVersion7();

        var held = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(held);

        await claims.ReleaseAsync(document, replica, held.Value, TestContext.Current.CancellationToken);

        var again = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(again);
        Assert.NotEqual(held, again);
    }

    [Fact]
    public async Task A_stale_session_cannot_release_the_claim_that_replaced_it()
    {
        // Why release is a compare-and-delete rather than a DEL. A process that
        // stalled past its claim's expiry wakes up and tidies away the claim of
        // the session that took over — and the replica then has two live
        // authors, which is the exact outcome the claim exists to prevent, now
        // caused by the cleanup.
        _redis.RequireRedis();
        var claims = Claims();
        var document = Guid.CreateVersion7();
        var replica = Guid.CreateVersion7();

        var stale = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(stale);

        await claims.ReleaseAsync(document, replica, stale.Value, TestContext.Current.CancellationToken);
        var live = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(live);

        // The stale session, unaware, tidies up.
        await claims.ReleaseAsync(document, replica, stale.Value, TestContext.Current.CancellationToken);

        // The live claim survived: nobody else can take it.
        var intruder = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.Null(intruder);
    }

    [Fact]
    public async Task A_stale_session_cannot_renew_the_claim_that_replaced_it()
    {
        // The same rule on the other verb, and the more dangerous direction: a
        // stale renewal would keep a dead session's claim alive indefinitely
        // while the live session believes it holds it.
        _redis.RequireRedis();
        var claims = Claims();
        var document = Guid.CreateVersion7();
        var replica = Guid.CreateVersion7();

        var stale = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(stale);
        await claims.ReleaseAsync(document, replica, stale.Value, TestContext.Current.CancellationToken);

        var live = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(live);

        Assert.False(await claims.RenewAsync(
            document, replica, stale.Value, TestContext.Current.CancellationToken));

        // And the holder's own renewal still works, so "renew refuses
        // everything" cannot pass this.
        Assert.True(await claims.RenewAsync(
            document, replica, live.Value, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unrenewed_claim_expires()
    {
        // The bound, tested where it is the only guarantee (§13.14). Release is
        // the fast path and it is switched off here: this claim is never
        // released, and the replica has to become claimable anyway, because the
        // case the TTL exists for is the process that died without releasing.
        _redis.RequireRedis();

        // The floor of §7's configured range would make this test a two-minute
        // wait, so the store is constructed directly with a short lifetime —
        // the options validation that enforces the range is a separate concern
        // and has its own test.
        var claims = Claims(TimeSpan.FromMilliseconds(600));
        var document = Guid.CreateVersion7();
        var replica = Guid.CreateVersion7();

        var held = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        Assert.NotNull(held);
        Assert.Null(await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken));

        // Polled against the wall clock rather than slept through. A single
        // delay assumes the scheduler waits the amount it was asked for, and a
        // sandbox that shortens timers turns this into a test that fails for a
        // reason having nothing to do with Redis. The deadline is what makes it
        // a real expiry rather than a busy loop that gives up.
        Guid? after = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (after is null && DateTime.UtcNow < deadline)
        {
            after = await claims.TryClaimAsync(document, replica, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(after);
    }

    [Fact]
    public void A_refresh_interval_at_or_past_the_lifetime_is_refused()
    {
        // A claim renewed no more often than it expires is a claim that expires,
        // and the failure mode is a live connection losing its replica to a
        // second session. Refused at construction rather than left to whoever
        // reads the configuration.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RedisReplicaClaims(
            _redis.Redis,
            new ReplicaClaimOptions
            {
                Lifetime = TimeSpan.FromMinutes(2),
                RefreshInterval = TimeSpan.FromMinutes(2),
            }));
    }
}
