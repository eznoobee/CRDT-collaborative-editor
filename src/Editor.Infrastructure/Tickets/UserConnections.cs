using StackExchange.Redis;

namespace Editor.Infrastructure.Tickets;

/// <summary>§7's per-user connection cap, held across instances.</summary>
public interface IUserConnections
{
    /// <summary>
    /// Takes a slot for <paramref name="replicaId"/>, or refuses because this
    /// user is at their cap.
    /// </summary>
    /// <remarks>
    /// Keyed on the replica rather than on the connection id because the slot
    /// is taken at <c>negotiate</c>, before a connection exists. It also gives
    /// resumption the behaviour it needs for free: a reload asks to continue
    /// the replica it already owns, so the same member is re-scored and no
    /// second slot is consumed. A reconnect storm from one tab costs one slot,
    /// not one per attempt.
    /// </remarks>
    Task<bool> TryAdmitAsync(Guid userId, Guid replicaId, CancellationToken cancellationToken);

    /// <summary>Says the slot is still in use. False when it had already gone.</summary>
    Task<bool> RenewAsync(Guid userId, Guid replicaId, CancellationToken cancellationToken);

    /// <summary>Gives the slot back.</summary>
    Task ReleaseAsync(Guid userId, Guid replicaId, CancellationToken cancellationToken);

    /// <summary>How many slots this user currently holds, stale ones excluded.</summary>
    Task<long> HeldAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// The cap in Redis, so it holds across instances (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// A sorted set per user: members are replica ids, scores are the last time
/// each said it was alive. Purging by score before every count is what makes
/// the structure self-healing — an instance that dies holding slots leaves
/// entries that age out, rather than a user locked out of their own account
/// until someone clears a key by hand. The alternative, a plain counter
/// incremented and decremented, has no way to distinguish a slot nobody
/// released from a slot in use, and drifts upward forever.
/// </para><para>
/// The purge, the count and the insert are one script because they must be
/// atomic together. Counting in one round trip and inserting in another is the
/// check-then-act race that lets two negotiates at the cap both succeed, and a
/// cap that admits one extra per concurrent request is not the cap §7 asks for.
/// </para><para>
/// Redis is the requirement rather than an implementation detail, for the same
/// reason as §7's rate limits: §8 forbids sticky sessions, so a per-process cap
/// is escaped by reconnecting until the load balancer picks another instance.
/// </para>
/// </remarks>
public sealed class RedisUserConnections : IUserConnections
{
    // KEYS[1] the user's set. ARGV: member, now, stale-before, cap, key TTL ms.
    private const string AdmitScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])
        if redis.call('ZSCORE', KEYS[1], ARGV[1]) == false then
          if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[4]) then
            return 0
          end
        end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return 1
        """;

    // A renewal never creates a slot. A connection whose entry has already aged
    // out has lost it, and inventing it back here would let a partitioned
    // instance's connections reappear over the cap.
    private const string RenewScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[3])
        if redis.call('ZSCORE', KEYS[1], ARGV[1]) == false then
          return 0
        end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[4])
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ConnectionLimitOptions _options;
    private readonly TimeProvider _time;

    public RedisUserConnections(
        IConnectionMultiplexer redis, ConnectionLimitOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _redis = redis;
        _options = options;
        _time = time;
    }

    public async Task<bool> TryAdmitAsync(
        Guid userId, Guid replicaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await _redis.GetDatabase()
            .ScriptEvaluateAsync(
                AdmitScript,
                [Key(userId)],
                [
                    replicaId.ToString("N"),
                    now,
                    now - (long)_options.StaleAfter.TotalMilliseconds,
                    _options.MaxPerUser,
                    (long)_options.StaleAfter.TotalMilliseconds * 2,
                ])
            .ConfigureAwait(false);

        return (long)result == 1;
    }

    public async Task<bool> RenewAsync(
        Guid userId, Guid replicaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await _redis.GetDatabase()
            .ScriptEvaluateAsync(
                RenewScript,
                [Key(userId)],
                [
                    replicaId.ToString("N"),
                    now,
                    now - (long)_options.StaleAfter.TotalMilliseconds,
                    (long)_options.StaleAfter.TotalMilliseconds * 2,
                ])
            .ConfigureAwait(false);

        return (long)result == 1;
    }

    public async Task ReleaseAsync(
        Guid userId, Guid replicaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _redis.GetDatabase()
            .SortedSetRemoveAsync(Key(userId), replicaId.ToString("N"))
            .ConfigureAwait(false);
    }

    public async Task<long> HeldAsync(Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var database = _redis.GetDatabase();
        var stale = _time.GetUtcNow().ToUnixTimeMilliseconds()
            - (long)_options.StaleAfter.TotalMilliseconds;

        // Purged before counting, so this answers the same question the
        // admission does. A count that included entries the admission would
        // have discarded would report a user at their cap who is not.
        await database.SortedSetRemoveRangeByScoreAsync(Key(userId), double.NegativeInfinity, stale)
            .ConfigureAwait(false);

        return await database.SortedSetLengthAsync(Key(userId)).ConfigureAwait(false);
    }

    private RedisKey Key(Guid userId) => $"{_options.KeyPrefix}user:{userId:N}";
}
