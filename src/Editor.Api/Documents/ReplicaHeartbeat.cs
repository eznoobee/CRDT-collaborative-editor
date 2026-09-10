using Editor.Api.Hubs;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>
/// Keeps <c>last_seen_at</c> current for the connections this instance holds
/// (PROJECT_SPEC.md §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Without this, retirement retires people who are sitting there
/// reading.</strong> <c>last_seen_at</c> was written by <c>negotiate</c> and by
/// nothing else, so it recorded when a replica <em>connected</em> and was read
/// by <see cref="ReplicaRetirement"/> as when it was last <em>active</em>. Those
/// are the same number for a session shorter than <c>T_retire</c> and different
/// for any session longer, and the difference is a live replica retired
/// underneath an open socket — after which the stability frontier advances past
/// state that replica still holds, which §5 says must never happen.
/// </para><para>
/// <strong>Attached to the connection, not to submission.</strong> Deriving
/// activity from writes would leave a viewer looking idle while they read, which
/// is §13.32's shape and the third time it has appeared in this system: a signal
/// attached to an action covers only the principals who take it.
/// </para><para>
/// One statement per tick rather than one per connection. At §8's thousand
/// connections an instance would otherwise write a thousand rows every
/// heartbeat; batched, it writes one statement naming them all, and the
/// heartbeat interval is minutes rather than seconds because the threshold it
/// feeds is seven days.
/// </para>
/// </remarks>
public sealed partial class ReplicaHeartbeat : BackgroundService
{
    private readonly DocumentConnections _connections;
    private readonly IServiceScopeFactory _scopes;
    private readonly ReplicaRetirementOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplicaHeartbeat> _logger;
    private long _touched;

    public ReplicaHeartbeat(
        DocumentConnections connections,
        IServiceScopeFactory scopes,
        IOptions<ReplicaRetirementOptions> options,
        TimeProvider time,
        ILogger<ReplicaHeartbeat> logger)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _connections = connections;
        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Replica rows this instance has refreshed since it started.</summary>
    /// <remarks>
    /// §13.15, and the same argument as <see cref="ReplicaRetirement.Retired"/>:
    /// a heartbeat that stopped running looks exactly like one with no
    /// connections to refresh, right up until a live replica is retired.
    /// </remarks>
    public long Touched => Interlocked.Read(ref _touched);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Heartbeat, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await BeatAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never allowed to end the loop. A heartbeat that stopped would
                // let every connection this instance holds age into retirement
                // while its users were still typing.
                Log.HeartbeatFailed(_logger, exception);
            }
        }
    }

    /// <summary>Refreshes every held connection's replica; answers how many.</summary>
    public async Task<int> BeatAsync(CancellationToken cancellationToken)
    {
        var live = _connections.Held().Select(held => held.ReplicaId).Distinct().ToArray();
        if (live.Length == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();

        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        // Retired rows are deliberately included. A replica that was retired
        // while genuinely live is a bug this heartbeat exists to prevent, and if
        // one happens anyway, refusing to touch it would leave it retired
        // forever. Clearing retired_at here would be worse — the frontier may
        // already have advanced past it — so the row is refreshed and the
        // replica is still told to resync when it reconnects.
        var touched = await context.DocumentReplicas
            .Where(replica => live.Contains(replica.ReplicaId))
            .ExecuteUpdateAsync(
                update => update.SetProperty(replica => replica.LastSeenAt, now),
                cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Add(ref _touched, touched);
        return touched;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3432,
            Level = LogLevel.Error,
            Message = "A replica heartbeat failed; held connections may age into retirement.")]
        public static partial void HeartbeatFailed(ILogger logger, Exception exception);
    }
}
