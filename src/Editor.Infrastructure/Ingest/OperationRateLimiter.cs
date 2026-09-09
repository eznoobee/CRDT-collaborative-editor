using StackExchange.Redis;

namespace Editor.Infrastructure.Ingest;

/// <summary>What a rate-limit check answers.</summary>
/// <param name="Allowed">Whether the submission may proceed.</param>
/// <param name="RetryAfter">
/// How long until the offending window rolls over. Zero when allowed.
/// </param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter)
{
    public static RateLimitDecision Ok() => new(true, TimeSpan.Zero);
}

/// <summary>§7's submission rate limits.</summary>
public interface IOperationRateLimiter
{
    /// <summary>
    /// Charges <paramref name="codePoints"/> against this user's and this
    /// connection's budgets.
    /// </summary>
    Task<RateLimitDecision> ChargeAsync(
        Guid userId, string connectionId, int codePoints, CancellationToken cancellationToken);
}

/// <summary>
/// The limits in Redis, so they hold across instances (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Redis is the requirement, not an implementation detail.</strong> §7
/// says the limits hold across instances, and a limiter that holds per process
/// is not a weaker version of that rule — it is a different rule, one an
/// attacker escapes by reconnecting until they land on another instance. §8
/// forbids sticky sessions, so that is not a rare accident; it is the normal
/// behaviour of the load balancer.
/// </para><para>
/// A fixed window rather than a sliding one, and the choice is worth stating.
/// A fixed window lets a caller spend two budgets across a window boundary, so
/// the real short-term ceiling is twice the configured rate. That is
/// acceptable here because the number bounds abuse rather than metering a
/// quota, and a sliding window costs a sorted set per key and a purge on every
/// submission — on the path §8 measures in milliseconds.
/// </para><para>
/// The increment and the expiry are one script because they must be atomic
/// together: an <c>INCRBY</c> that succeeded and an <c>EXPIRE</c> that did not
/// leaves a counter with no lifetime, which stops the window ever rolling over
/// and locks the user out permanently. The expiry is set only on the increment
/// that creates the key, so a burst cannot keep pushing the window forward.
/// </para>
/// </remarks>
public sealed class RedisOperationRateLimiter : IOperationRateLimiter
{
    private const string ChargeScript = """
        local total = redis.call('INCRBY', KEYS[1], ARGV[1])
        if total == tonumber(ARGV[1]) then
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        local ttl = redis.call('PTTL', KEYS[1])
        return {total, ttl}
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly RateLimitOptions _options;

    public RedisOperationRateLimiter(IConnectionMultiplexer redis, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        _redis = redis;
        _options = options;
    }

    public async Task<RateLimitDecision> ChargeAsync(
        Guid userId, string connectionId, int codePoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        cancellationToken.ThrowIfCancellationRequested();

        if (codePoints <= 0)
        {
            return RateLimitDecision.Ok();
        }

        // Both budgets are charged, and both before either is judged. Charging
        // one and returning early on the other would let a caller who is over
        // their connection budget avoid consuming their user budget, which is
        // the wrong way round: the expensive request happened either way.
        var user = await ChargeOneAsync($"{_options.KeyPrefix}user:{userId:N}", codePoints)
            .ConfigureAwait(false);
        var connection = await ChargeOneAsync($"{_options.KeyPrefix}conn:{connectionId}", codePoints)
            .ConfigureAwait(false);

        var overUser = user.Total > _options.CodePointsPerUser;
        var overConnection = connection.Total > _options.CodePointsPerConnection;

        if (!overUser && !overConnection)
        {
            return RateLimitDecision.Ok();
        }

        var wait = overUser && overConnection
            ? (user.Ttl > connection.Ttl ? user.Ttl : connection.Ttl)
            : overUser ? user.Ttl : connection.Ttl;

        return new RateLimitDecision(false, wait);
    }

    private async Task<(long Total, TimeSpan Ttl)> ChargeOneAsync(string key, int codePoints)
    {
        var result = (RedisResult[]?)await _redis.GetDatabase()
            .ScriptEvaluateAsync(
                ChargeScript,
                [key],
                [codePoints, (long)_options.Interval.TotalMilliseconds])
            .ConfigureAwait(false);

        if (result is null || result.Length < 2)
        {
            // The script always returns two values, so this is unreachable
            // short of a Redis fault. Refusing the submission on a fault would
            // make a Redis blip look like abuse to the user; charging nothing
            // and allowing it keeps typing working, and §7's other checks are
            // unaffected. Stated because "fail open" is a choice that has to
            // be a deliberate one.
            return (0, _options.Interval);
        }

        var total = (long)result[0];
        var ttl = (long)result[1];

        // A negative TTL means no expiry was observed, which the script makes
        // impossible on the creating increment; fall back to the full interval
        // rather than telling a client to retry immediately.
        return (total, ttl > 0 ? TimeSpan.FromMilliseconds(ttl) : _options.Interval);
    }
}
