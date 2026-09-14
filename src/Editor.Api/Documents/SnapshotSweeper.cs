using Editor.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>When §6's periodic snapshot runs.</summary>
public sealed class SnapshotOptions
{
    public const string Section = "Snapshots";

    /// <summary>Operations between snapshots (§6, default 500).</summary>
    /// <remarks>
    /// Zero disables snapshotting, which is a supported configuration and not a
    /// mistake: a deployment small enough that full replay is cheap pays nothing
    /// for it. It is also the value an unconfigured
    /// <see cref="SnapshotPolicy"/> reads as, which is why this is bound
    /// explicitly rather than defaulted through the struct.
    /// </remarks>
    public int OperationsPerSnapshot { get; set; } = 500;

    /// <summary>How often a sweep looks for documents that have fallen behind.</summary>
    /// <remarks>
    /// A minute rather than the collector's five. The cost of being late here is
    /// paid by whoever opens the document next, as replay time against §8's
    /// 500 ms load target; the cost of being late to collect is disk.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Documents examined per sweep.</summary>
    public int BatchSize { get; set; } = 16;
}

/// <summary>
/// Runs §6's periodic snapshot, which until 7b.3 nothing did (register row 28).
/// </summary>
/// <remarks>
/// <para>
/// The scheduling half of <see cref="IPeriodicSnapshotter"/>, split the same way
/// <see cref="TombstoneCollector"/> is split from the collector: what to write
/// is a question about documents and belongs below the API, when to write is a
/// question about this process.
/// </para><para>
/// <strong>§13.41 is the whole point of this file.</strong> The snapshot policy
/// and the writer both existed and both had tests; what did not exist was
/// anything that called them in the running server, and no test of either could
/// have shown that. The test for this one asserts a snapshot row appears from
/// ordinary submission traffic with nothing in the test calling
/// <see cref="DocumentStore.SaveSnapshotAsync"/>.
/// </para>
/// </remarks>
public sealed partial class SnapshotSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SnapshotOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SnapshotSweeper> _logger;
    private long _written;
    private long _sweeps;
    private long _worstAgeTicks;

    public SnapshotSweeper(
        IServiceScopeFactory scopes,
        IOptions<SnapshotOptions> options,
        TimeProvider time,
        ILogger<SnapshotSweeper> logger)
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

    /// <summary>Snapshots this instance has written since it started.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Sweeps completed, whether or not they wrote anything.</summary>
    public long Sweeps => Interlocked.Read(ref _sweeps);

    /// <summary>
    /// §10's snapshot age: the oldest latest-snapshot the last sweep saw.
    /// </summary>
    /// <remarks>
    /// Read from the last sweep rather than queried per scrape. A gauge that
    /// runs a scan across every document each time a collector asks is a way to
    /// make observability the thing that falls over first, and the number is
    /// only ever as fresh as the policy that maintains it anyway.
    /// </remarks>
    public TimeSpan WorstSnapshotAge => TimeSpan.FromTicks(Interlocked.Read(ref _worstAgeTicks));

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

    /// <summary>Snapshots a batch of documents; answers how many were written.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var snapshotter = scope.ServiceProvider.GetRequiredService<IPeriodicSnapshotter>();

        var sweep = await snapshotter
            .SnapshotAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Exchange(ref _worstAgeTicks, sweep.WorstAge.Ticks);
        Interlocked.Increment(ref _sweeps);

        if (sweep.Written > 0)
        {
            Interlocked.Add(ref _written, sweep.Written);
            Log.Snapshotted(_logger, sweep.Written);
        }

        return sweep.Written;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3450,
            Level = LogLevel.Information,
            Message = "Wrote {Count} periodic snapshots.")]
        public static partial void Snapshotted(ILogger logger, int count);

        [LoggerMessage(
            EventId = 3451,
            Level = LogLevel.Error,
            Message = "A snapshot sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
