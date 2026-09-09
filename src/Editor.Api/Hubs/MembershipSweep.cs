using Editor.Infrastructure.Authorization;
using Microsoft.Extensions.Options;

namespace Editor.Api.Hubs;

/// <summary>How often live connections are re-checked against membership.</summary>
public sealed class MembershipSweepOptions
{
    public const string Section = "MembershipSweep";

    /// <summary>
    /// The gap between sweeps. Added to the role cache's TTL, it must stay
    /// inside §7's five-second revocation bound.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Closes the connections of members who no longer are (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// §7's role check runs on every submission, which covers a revoked
/// <em>writer</em> and does nothing about a revoked <em>reader</em>: a client
/// that only receives sends nothing, so nothing re-checks it, and it goes on
/// receiving every broadcast on the document for as long as its socket stays
/// open. A revocation that leaves the revoked party still reading the text is
/// not a revocation, whatever the writes do.
/// </para><para>
/// Demotion is not the same event and is deliberately not handled here. A
/// member downgraded to viewer keeps their connection and keeps receiving, and
/// their next write is refused with <c>forbidden</c> — which is exactly what §9's
/// table says that code means. Only the loss of every role closes the socket.
/// </para><para>
/// <strong>The bound is structural rather than a comment.</strong> Worst case
/// from revocation to close is the role cache's TTL plus this interval, so the
/// constructor refuses a configuration where those two exceed §7's five
/// seconds. Set them so they cannot, and the guarantee survives the pub/sub
/// channel being down — which matters, because that channel is explicitly best
/// effort (§13.31: the eager path is an optimisation over the TTL, never a
/// substitute for it).
/// </para>
/// </remarks>
public sealed partial class MembershipSweep : BackgroundService
{
    private readonly DocumentConnections _connections;
    private readonly IDocumentRoles _roles;
    private readonly MembershipSweepOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MembershipSweep> _logger;
    private long _revoked;

    public MembershipSweep(
        DocumentConnections connections,
        IDocumentRoles roles,
        IOptions<MembershipSweepOptions> options,
        IOptions<DocumentRoleCacheOptions> cache,
        TimeProvider time,
        ILogger<MembershipSweep> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        var interval = options.Value.Interval;
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), interval, "The sweep interval must be positive.");
        }

        // The check that makes §7's bound a property of the configuration
        // rather than of whoever last edited two numbers in different files.
        var worstCase = interval + cache.Value.Ttl;
        if (worstCase > DocumentRoleCacheOptions.MaximumTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                worstCase,
                $"A revoked connection would survive up to {worstCase}, and §7 bounds "
                + $"revocation at {DocumentRoleCacheOptions.MaximumTtl}. Lower the sweep "
                + "interval or the role cache TTL.");
        }

        _connections = connections;
        _roles = roles;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Connections closed because their membership was gone.</summary>
    /// <remarks>
    /// §13.15: a sweep that silently stopped sweeping and one that works look
    /// identical from outside until someone is revoked, and by then nobody is
    /// watching. Counted so a test can assert the mechanism rather than infer it
    /// from a socket that closed for some reason.
    /// </remarks>
    public long RevokedConnections => Interlocked.Read(ref _revoked);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never allowed to end the loop: a sweep that stopped on one bad
                // tick would leave every revoked connection open, and the only
                // symptom would be someone reading a document they were removed
                // from.
                Log.SweepFailed(_logger, exception);
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var held in _connections.Held())
        {
            var role = await _roles
                .GetRoleAsync(held.DocumentId, held.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (role is not null)
            {
                continue;
            }

            Interlocked.Increment(ref _revoked);
            Log.MembershipGone(_logger, held.DocumentId, held.ReplicaId);
            _connections.Abort(held.DocumentId, held.ConnectionId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3430,
            Level = LogLevel.Error,
            Message = "The membership sweep failed for this instance's connections.")]
        public static partial void SweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 3431,
            Level = LogLevel.Warning,
            Message = "Membership is gone for replica {ReplicaId} of document {DocumentId}; closing its connection.")]
        public static partial void MembershipGone(ILogger logger, Guid documentId, Guid replicaId);
    }
}
