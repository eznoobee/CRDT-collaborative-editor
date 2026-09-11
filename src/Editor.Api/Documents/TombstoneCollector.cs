using Editor.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>When §5's tombstone collection runs.</summary>
public sealed class TombstoneCollectionOptions
{
    public const string Section = "TombstoneCollection";

    /// <summary>How often a sweep looks for documents worth collecting.</summary>
    /// <remarks>
    /// GC is never on the request path (§5). The interval is a throughput
    /// question rather than a correctness one: nothing is wrong while a
    /// tombstone waits, and the cost of running often is a scan per document.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Documents examined per sweep.</summary>
    public int BatchSize { get; set; } = 32;
}

/// <summary>
/// Collects causally stable tombstones under §5's four rules.
/// </summary>
/// <remarks>
/// <para>
/// The only operation in this system that destroys data, and the only one whose
/// mistakes converge: a collected element that is still referenced resolves to
/// nothing, the algorithm is total, and every replica agrees on text that is
/// wrong. §5 states what that means for how this is tested.
/// </para><para>
/// <strong>The counters are the trigger evidence.</strong> §13.41 — a test that
/// calls <see cref="SweepAsync"/> proves collection works and says nothing
/// about whether anything ever calls it, and GC is the subsystem where that
/// distinction costs most: a collector that never runs is indistinguishable
/// from a healthy one until disks fill, while a collector that runs wrongly is
/// indistinguishable from a healthy one forever.
/// </para>
/// </remarks>
public sealed partial class TombstoneCollector : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TombstoneCollectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TombstoneCollector> _logger;
    private long _collected;
    private long _sweeps;

    public TombstoneCollector(
        IServiceScopeFactory scopes,
        IOptions<TombstoneCollectionOptions> options,
        TimeProvider time,
        ILogger<TombstoneCollector> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Elements this instance has collected since it started.</summary>
    public long Collected => Interlocked.Read(ref _collected);

    /// <summary>Sweeps completed, whether or not they collected anything.</summary>
    public long Sweeps => Interlocked.Read(ref _sweeps);

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
                Log.SweepFailed(_logger, exception);
            }
        }
    }

    /// <summary>Collects across a batch of documents; answers how many elements went.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISnapshotGarbageCollector>();

        var collected = await store
            .CollectAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _sweeps);

        if (collected > 0)
        {
            Interlocked.Add(ref _collected, collected);
            Log.Collected(_logger, collected);
        }

        return collected;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3440,
            Level = LogLevel.Information,
            Message = "Collected {Count} causally stable tombstones.")]
        public static partial void Collected(ILogger logger, int count);

        [LoggerMessage(
            EventId = 3441,
            Level = LogLevel.Error,
            Message = "A tombstone collection sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
