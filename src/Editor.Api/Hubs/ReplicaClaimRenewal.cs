using Editor.Infrastructure.Tickets;
using Microsoft.Extensions.Options;

namespace Editor.Api.Hubs;

/// <summary>
/// Keeps this instance's replica claims alive while it holds their connections
/// (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// A claim expires so that a process which dies holding one cannot strand a
/// replica — its owner would be locked out of their own work until an operator
/// intervened. The cost of that bound is that a live connection has to say it is
/// still there, and this is what says it.
/// </para>
/// <para>
/// A timer over every connection this instance holds, rather than a renewal
/// hung off each submission. Renewing on activity is the tempting version and it
/// is wrong in exactly the case that matters: a client reading a document
/// without typing sends nothing for minutes, so its claim would lapse while the
/// socket is still open — and a second <c>negotiate</c> could then resume a
/// replica that still has a live author, which is the one thing §5's tie-break
/// cannot survive.
/// </para>
/// <para>
/// A renewal that comes back false has lost the race: something else holds the
/// claim now. The connection is closed rather than left running, because the
/// alternative is two live authors under one replica id, and §13.13 says the
/// client has to be able to see it happen.
/// </para>
/// </remarks>
public sealed partial class ReplicaClaimRenewal : BackgroundService
{
    private readonly DocumentConnections _connections;
    private readonly IReplicaClaims _claims;
    private readonly ReplicaClaimOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplicaClaimRenewal> _logger;
    private long _lost;

    public ReplicaClaimRenewal(
        DocumentConnections connections,
        IReplicaClaims claims,
        IOptions<ReplicaClaimOptions> options,
        TimeProvider time,
        ILogger<ReplicaClaimRenewal> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _claims = claims;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Connections dropped because their claim was taken by someone else.
    /// </summary>
    /// <remarks>
    /// §13.15. A renewal loop that silently stopped renewing and one that works
    /// look identical until a claim lapses, and by then the symptom is a
    /// reconnect nobody can explain. Above zero means either a Redis problem or
    /// a genuine double-claim, and both are worth knowing about.
    /// </remarks>
    public long LostClaims => Interlocked.Read(ref _lost);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.RefreshInterval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RenewAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never allowed to end the loop. A renewal service that stopped
                // on one bad tick would let every claim on this instance lapse,
                // and nothing else would notice.
                Log.RenewalFailed(_logger, exception);
            }
        }
    }

    private async Task RenewAllAsync(CancellationToken cancellationToken)
    {
        foreach (var held in _connections.Held())
        {
            var renewed = await _claims
                .RenewAsync(held.DocumentId, held.ReplicaId, held.ClaimToken, cancellationToken)
                .ConfigureAwait(false);

            if (renewed)
            {
                continue;
            }

            Interlocked.Increment(ref _lost);
            Log.ClaimLost(_logger, held.DocumentId, held.ReplicaId);
            _connections.Abort(held.DocumentId, held.ConnectionId);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3420,
            Level = LogLevel.Error,
            Message = "Replica claim renewal failed for this instance's connections.")]
        public static partial void RenewalFailed(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 3421,
            Level = LogLevel.Warning,
            Message = "Lost the claim on replica {ReplicaId} of document {DocumentId}; closing its connection.")]
        public static partial void ClaimLost(ILogger logger, Guid documentId, Guid replicaId);
    }
}
