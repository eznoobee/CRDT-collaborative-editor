using System.Collections.Concurrent;
using System.Globalization;
using Editor.Domain;
using StackExchange.Redis;

// StackExchange.Redis has a Role type of its own, for replication topology.
using Role = Editor.Domain.Role;

namespace Editor.Infrastructure.Authorization;

/// <summary>
/// §7's second authorization check: the caller's role, cached with a bound.
/// </summary>
/// <remarks>
/// Three tiers, for three different costs.
/// <list type="number">
/// <item>An in-process copy, because §8 rules out a network hop per keystroke
/// per connection just as firmly as it rules out a database one.</item>
/// <item>Redis, so an instance that has never seen this pair does not go to
/// Postgres, and so a fleet does not stampede the database after a
/// deploy.</item>
/// <item>Postgres, the source of truth.</item>
/// </list>
/// <para>
/// Both cached tiers expire after the same bound, so the worst case is §7's
/// five seconds even if every invalidation message is lost. Pub/sub makes the
/// usual case immediate; it is an optimisation over the TTL, not a substitute
/// for it, and treating a delivered message as the mechanism would make
/// revocation depend on a channel with no delivery guarantee.
/// </para>
/// </remarks>
public sealed class CachedDocumentRoles : IDocumentRoles, IAsyncDisposable
{
    /// <summary>What is cached when the answer is "no role at all".</summary>
    /// <remarks>
    /// Absence has to be cached too. Without it, every operation from a caller
    /// who lost access — or from an attacker probing document ids — is a
    /// database read, which is a denial-of-service the cache was supposed to
    /// prevent.
    /// </remarks>
    private const string NoRole = "none";

    private readonly ConcurrentDictionary<(Guid Document, Guid User), Entry> _local = new();
    private readonly IConnectionMultiplexer _redis;
    private readonly IDocumentRoles _source;
    private readonly DocumentRoleCacheOptions _options;
    private readonly TimeProvider _time;
    private ChannelMessageQueue? _invalidations;

    public CachedDocumentRoles(
        IConnectionMultiplexer redis,
        IDocumentRoles source,
        DocumentRoleCacheOptions options,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        // Enforced here as well as by options validation, because this is the
        // guard a direct caller cannot route around. A TTL above §7's bound is
        // a revocation that takes longer than §7 allows.
        if (options.Ttl <= TimeSpan.Zero || options.Ttl > DocumentRoleCacheOptions.MaximumTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Ttl,
                $"§7 bounds role staleness at {DocumentRoleCacheOptions.MaximumTtl}.");
        }

        _redis = redis;
        _source = source;
        _options = options;
        _time = time;
    }

    /// <summary>Starts listening for eager invalidations.</summary>
    /// <remarks>
    /// Best effort, and deliberately not fatal. Redis being unreachable at
    /// startup is a readiness question (§10), not a reason for the process to
    /// refuse to come up — a fleet that will not start while Redis restarts
    /// turns a brief dependency outage into an outage. Losing the subscription
    /// costs latency and not correctness: without a single invalidation
    /// message, revocation still lands inside §7's five seconds because that
    /// is what the TTL is for. The subscription is retried when the
    /// multiplexer reconnects.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _redis.ConnectionRestored += OnConnectionRestored;
        await TrySubscribeAsync().ConfigureAwait(false);
    }

    private async Task TrySubscribeAsync()
    {
        if (_invalidations is not null)
        {
            return;
        }

        try
        {
            var queue = await _redis.GetSubscriber()
                .SubscribeAsync(RedisChannel.Literal(_options.InvalidationChannel))
                .ConfigureAwait(false);

            queue.OnMessage(message => Forget(message.Message.ToString()));
            _invalidations = queue;
        }
        catch (RedisException)
        {
            // Left unsubscribed; ConnectionRestored will bring it back. The
            // five-second TTL is the guarantee in the meantime.
            _invalidations = null;
        }
        catch (ObjectDisposedException)
        {
            // Shutting down while a reconnect was in flight.
            _invalidations = null;
        }
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e) =>
        _ = TrySubscribeAsync();


    public async Task<Role?> GetRoleAsync(
        Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        var key = (documentId, userId);
        var now = _time.GetUtcNow();

        if (_local.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Role;
        }

        var database = _redis.GetDatabase();

        // With the expiry, not without it. Refreshing the local copy for a full
        // TTL from a Redis entry that has one second left would let a role
        // survive for nearly twice §7's bound: local expires, reads Redis just
        // before Redis does, and starts another five seconds. The local copy
        // must never outlive the shared one.
        var stored = await database.StringGetWithExpiryAsync(RedisKey(documentId, userId))
            .ConfigureAwait(false);

        if (!stored.Value.IsNullOrEmpty && TryParse(stored.Value.ToString(), out var shared))
        {
            var remaining = stored.Expiry ?? _options.Ttl;
            if (remaining > TimeSpan.Zero)
            {
                _local[key] = new Entry(shared, now + Min(remaining, _options.Ttl));
                return shared;
            }
        }

        var authoritative = await _source
            .GetRoleAsync(documentId, userId, cancellationToken)
            .ConfigureAwait(false);

        await database.StringSetAsync(
            RedisKey(documentId, userId), Format(authoritative), _options.Ttl).ConfigureAwait(false);

        _local[key] = new Entry(authoritative, now + _options.Ttl);
        return authoritative;
    }

    /// <summary>
    /// Drops a cached role everywhere: here, in Redis, and on every other
    /// instance.
    /// </summary>
    public async Task InvalidateAsync(
        Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Redis first. If the publish is what fails, instances fall back to the
        // TTL and read a Redis key that is already gone; if the delete were
        // second, a losing race could put the old value back.
        await _redis.GetDatabase().KeyDeleteAsync(RedisKey(documentId, userId)).ConfigureAwait(false);

        var payload = Payload(documentId, userId);
        Forget(payload);

        // Fire and forget: this is the optimisation, and a publish that cannot
        // be delivered must not fail the membership change that already
        // committed. Instances fall back to the TTL.
        await _redis.GetSubscriber()
            .PublishAsync(
                RedisChannel.Literal(_options.InvalidationChannel), payload, CommandFlags.FireAndForget)
            .ConfigureAwait(false);
    }

    /// <summary>Stops listening, tolerating a multiplexer that is already gone.</summary>
    /// <remarks>
    /// Nothing here orders this against the multiplexer's own disposal. Both are
    /// singletons, the container disposes them in whatever order it likes, and
    /// on a slow shutdown this loses the race and throws
    /// <see cref="ObjectDisposedException"/> from inside a hosted service's
    /// StopAsync — which fails the host's shutdown, and in a test host fails
    /// whichever test happened to be running.
    /// <para>
    /// Unsubscribing from a connection that no longer exists is not a failure,
    /// it is the outcome already achieved, so it is swallowed rather than
    /// ordered around. The alternative — making this depend on disposal
    /// order — would be a guarantee the container does not offer.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _redis.ConnectionRestored -= OnConnectionRestored;

            if (_invalidations is not null)
            {
                await _invalidations.UnsubscribeAsync().ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // The multiplexer went first. Nothing left to unsubscribe from.
        }
        catch (RedisException)
        {
            // Redis is unreachable. The subscription dies with the connection.
        }
        finally
        {
            _invalidations = null;
        }
    }

    private void Forget(string? payload)
    {
        if (payload is null)
        {
            return;
        }

        var separator = payload.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0
            || !Guid.TryParse(payload.AsSpan(0, separator), out var documentId)
            || !Guid.TryParse(payload.AsSpan(separator + 1), out var userId))
        {
            // Someone else's message on a shared channel. Dropping the whole
            // local cache on an unparseable payload would let any client with
            // Redis access flush every instance.
            return;
        }

        _local.TryRemove((documentId, userId), out _);
    }

    private static string Payload(Guid documentId, Guid userId) =>
        string.Create(CultureInfo.InvariantCulture, $"{documentId:D}:{userId:D}");

    private string RedisKey(Guid documentId, Guid userId) =>
        string.Create(CultureInfo.InvariantCulture, $"{_options.KeyPrefix}{documentId:N}:{userId:N}");

    private static string Format(Role? role) =>
        role is null ? NoRole : ((int)role.Value).ToString(CultureInfo.InvariantCulture);

    private static bool TryParse(string stored, out Role? role)
    {
        if (string.Equals(stored, NoRole, StringComparison.Ordinal))
        {
            role = null;
            return true;
        }

        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && Enum.IsDefined((Role)value))
        {
            role = (Role)value;
            return true;
        }

        // An unrecognised value is not a role. Falling through to Postgres is
        // correct and cheap; guessing would grant whatever the bytes decoded to.
        role = null;
        return false;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private readonly record struct Entry(Role? Role, DateTimeOffset ExpiresAt);
}
