using Editor.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>When the log truncation sweep runs.</summary>
public sealed class LogTruncationOptions
{
    public const string Section = "LogTruncation";

    /// <summary>How often a sweep looks for rows to reclaim.</summary>
    /// <remarks>
    /// Slower than collection's, deliberately. Collection is the correctness
    /// half and is reversible — it shrinks a snapshot, which is a cache of the
    /// replay. This is the step that makes it permanent, and it reads a whole
    /// snapshot back before it acts, so running it often costs a decode per
    /// document to reclaim rows that were not going anywhere.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Documents examined per sweep.</summary>
    public int BatchSize { get; set; } = 16;
}

/// <summary>
/// Reclaims the log rows behind a collected snapshot (register row 30).
/// </summary>
/// <remarks>
/// <para>
/// <b>The second operation in this system that destroys data</b>, and unlike
/// collection it cannot be undone by rebuilding from the log — it is the log.
/// What it removes and why that set is not a prefix is in
/// <see cref="LogTruncator"/>; this is only the schedule.
/// </para><para>
/// <b>The counters are the trigger evidence</b> (§13.41, §12's question 2). A
/// test that calls <see cref="SweepAsync"/> proves truncation works and says
/// nothing about whether anything ever calls it — and this is the subsystem
/// where that distinction costs most in both directions: a sweep that never
/// runs is indistinguishable from a healthy one until disks fill, and a sweep
/// that runs wrongly is indistinguishable from a healthy one until a client is
/// told to throw its work away.
/// </para>
/// </remarks>
public sealed partial class LogTruncationSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LogTruncationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<LogTruncationSweeper> _logger;
    private long _removed;
    private long _sweeps;

    public LogTruncationSweeper(
        IServiceScopeFactory scopes,
        IOptions<LogTruncationOptions> options,
        TimeProvider time,
        ILogger<LogTruncationSweeper> logger)
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

    /// <summary>Rows this instance has reclaimed since it started.</summary>
    public long Removed => Interlocked.Read(ref _removed);

    /// <summary>Sweeps completed, whether or not they reclaimed anything.</summary>
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

    /// <summary>Truncates across a batch of documents; answers how many rows went.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var truncator = scope.ServiceProvider.GetRequiredService<ILogTruncator>();

        var removed = await truncator
            .TruncateAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _sweeps);

        if (removed > 0)
        {
            Interlocked.Add(ref _removed, removed);
            Log.Truncated(_logger, removed);
        }

        return removed;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3450,
            Level = LogLevel.Information,
            Message = "Reclaimed {Count} operation-log rows behind collected snapshots.")]
        public static partial void Truncated(ILogger logger, int count);

        [LoggerMessage(
            EventId = 3451,
            Level = LogLevel.Error,
            Message = "A log truncation sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
