using System.ComponentModel.DataAnnotations;
using StackExchange.Redis;

namespace Editor.Infrastructure.Tickets;

/// <summary>How long an unrefreshed replica claim survives (§7).</summary>
public sealed class ReplicaClaimOptions
{
    public const string Section = "ReplicaClaims";

    /// <summary>
    /// How long a claim lives without a refresh.
    /// </summary>
    /// <remarks>
    /// Bounded because a process that dies holding a claim must not strand the
    /// replica: the owner would be unable to resume their own work until an
    /// operator intervened. It must comfortably exceed §7's 60-second ticket
    /// lifetime, because the claim is taken at <c>negotiate</c> and the
    /// connection that will refresh it does not exist yet.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:30", "00:10:00")]
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often a live connection renews its claim.
    /// </summary>
    /// <remarks>
    /// Well under <see cref="Lifetime"/>, so a single missed refresh — a GC
    /// pause, a Redis blip — does not drop a claim a live connection is still
    /// holding. Losing it while connected is the one outcome that breaks the
    /// invariant, because a second negotiate could then resume a replica that
    /// still has a live author.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:02:00")]
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Who, if anyone, is currently authoring as a given replica (§7).</summary>
public interface IReplicaClaims
{
    /// <summary>
    /// Claims a replica for one session, if nothing else holds it.
    /// </summary>
    /// <returns>
    /// An opaque token proving ownership, or <see langword="null"/> when the
    /// replica is already claimed.
    /// </returns>
    Task<Guid?> TryClaimAsync(Guid documentId, Guid replicaId, CancellationToken cancellationToken);

    /// <summary>Extends a claim this session holds. False when it no longer holds it.</summary>
    Task<bool> RenewAsync(Guid documentId, Guid replicaId, Guid token, CancellationToken cancellationToken);

    /// <summary>Releases a claim this session holds, and nobody else's.</summary>
    Task ReleaseAsync(Guid documentId, Guid replicaId, Guid token, CancellationToken cancellationToken);

    /// <summary>Whether anyone currently holds this replica.</summary>
    /// <remarks>
    /// Observation, never a decision: resumption turns on
    /// <see cref="TryClaimAsync"/> succeeding, because a check followed by a
    /// claim is two operations and the gap between them is the race the claim
    /// exists to close. This exists so a test can wait for a release to have
    /// happened rather than for a proxy that correlates with it — which is how
    /// a test ends up passing because the wrong check refused (§13.18).
    /// </remarks>
    Task<bool> IsHeldAsync(Guid documentId, Guid replicaId, CancellationToken cancellationToken);
}

/// <summary>
/// Replica claims in Redis (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// §7 lets a client resume a replica it owns, which is what makes a reload keep
/// its outbox — and requires that no two live sessions author as one replica.
/// §5's tie-break assumes one author per id: two of them can mint two different
/// operations carrying the same <c>ElementId</c>, and every peer converges on
/// whichever it saw first, differently.
/// </para>
/// <para>
/// The claim is taken at <c>negotiate</c> rather than at connect. The ticket
/// exists before the connection does, so a claim taken when the socket opens
/// leaves a window in which two negotiate calls both succeed for one replica and
/// the collision only appears later. Redis rather than memory for §8's reason:
/// the instance issuing the ticket is usually not the one holding the socket.
/// </para>
/// <para>
/// Release and renew are compare-and-act against the token, in Lua, so a session
/// cannot extend or drop a claim that a later session has taken over after its
/// own expired. A plain DEL would let a stalled process delete the live
/// claim of the session that replaced it.
/// </para>
/// </remarks>
public sealed class RedisReplicaClaims : IReplicaClaims
{
    private const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IConnectionMultiplexer _redis;
    private readonly ReplicaClaimOptions _options;

    public RedisReplicaClaims(IConnectionMultiplexer redis, ReplicaClaimOptions options)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);

        if (options.RefreshInterval >= options.Lifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RefreshInterval,
                "A claim refreshed no more often than it expires is a claim that expires.");
        }

        _redis = redis;
        _options = options;
    }

    public async Task<Guid?> TryClaimAsync(
        Guid documentId, Guid replicaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var token = Guid.CreateVersion7();
        var taken = await _redis.GetDatabase()
            .StringSetAsync(
                Key(documentId, replicaId), Value(token), _options.Lifetime, When.NotExists)
            .ConfigureAwait(false);

        return taken ? token : null;
    }

    public async Task<bool> RenewAsync(
        Guid documentId, Guid replicaId, Guid token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await _redis.GetDatabase().ScriptEvaluateAsync(
            RenewScript,
            [Key(documentId, replicaId)],
            [Value(token), (long)_options.Lifetime.TotalMilliseconds]).ConfigureAwait(false);

        return (long)result == 1;
    }

    public async Task ReleaseAsync(
        Guid documentId, Guid replicaId, Guid token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _redis.GetDatabase().ScriptEvaluateAsync(
            ReleaseScript,
            [Key(documentId, replicaId)],
            [Value(token)]).ConfigureAwait(false);
    }

    public async Task<bool> IsHeldAsync(
        Guid documentId, Guid replicaId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await _redis.GetDatabase()
            .KeyExistsAsync(Key(documentId, replicaId))
            .ConfigureAwait(false);
    }

    private static RedisKey Key(Guid documentId, Guid replicaId) =>
        $"editor:replica-claim:{documentId:N}:{replicaId:N}";

    private static RedisValue Value(Guid token) => token.ToString("N");
}
