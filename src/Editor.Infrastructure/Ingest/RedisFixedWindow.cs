using StackExchange.Redis;

namespace Editor.Infrastructure.Ingest;

/// <summary>What one charge against a fixed window answers.</summary>
/// <param name="Total">The window's total after this charge.</param>
/// <param name="Ttl">How long until the window rolls over.</param>
public readonly record struct WindowCharge(long Total, TimeSpan Ttl);

/// <summary>
/// A fixed counting window in Redis, shared by §7's rate limits.
/// </summary>
/// <remarks>
/// <para>
/// One implementation rather than one per limit. §7 has two rate limits with
/// different units — code points on submission, requests on the document API —
/// and the *unit* is the only thing that differs; the window, the atomicity and
/// the expiry are identical. Two copies of this script would be two places to
/// get the expiry wrong, and the failure mode of getting it wrong is a counter
/// with no lifetime, which never rolls over and locks the caller out
/// permanently.
/// </para><para>
/// The increment and the expiry are one script because they must be atomic
/// together, and the expiry is set only on the increment that creates the key,
/// so a sustained burst cannot keep pushing the window forward.
/// </para><para>
/// A fixed window rather than a sliding one, and the choice is worth stating: a
/// fixed window lets a caller spend two budgets across a boundary, so the real
/// short-term ceiling is twice the configured rate. That is acceptable because
/// these numbers bound abuse rather than metering a quota, and a sliding window
/// costs a sorted set per key and a purge on every request.
/// </para>
/// </remarks>
public sealed class RedisFixedWindow
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

    public RedisFixedWindow(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
    }

    /// <summary>Adds <paramref name="amount"/> to <paramref name="key"/>'s window.</summary>
    public async Task<WindowCharge> ChargeAsync(string key, long amount, TimeSpan interval)
    {
        var result = (RedisResult[]?)await _redis.GetDatabase()
            .ScriptEvaluateAsync(
                ChargeScript, [key], [amount, (long)interval.TotalMilliseconds])
            .ConfigureAwait(false);

        if (result is null || result.Length < 2)
        {
            // The script always returns two values, so this is unreachable
            // short of a Redis fault. Refusing on a fault would make a Redis
            // blip look like abuse to the user; charging nothing and allowing
            // it keeps the application working, and §7's other checks are
            // unaffected. Stated because "fail open" has to be a decision.
            return new WindowCharge(0, interval);
        }

        var total = (long)result[0];
        var ttl = (long)result[1];

        // A negative TTL means no expiry was observed, which the script makes
        // impossible on the creating increment; fall back to the full interval
        // rather than telling a caller to retry immediately.
        return new WindowCharge(total, ttl > 0 ? TimeSpan.FromMilliseconds(ttl) : interval);
    }
}
