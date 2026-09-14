using Microsoft.Extensions.Options;
using Npgsql;

namespace Editor.Api.Infrastructure;

/// <summary>How often the state-derived readings are refreshed.</summary>
public sealed class StateReadingOptions
{
    public const string Section = "StateReadings";

    /// <summary>
    /// Interval between reads.
    /// </summary>
    /// <remarks>
    /// A query per interval rather than a query per scrape: a gauge that runs
    /// an aggregate every time a collector asks makes observability the thing
    /// that falls over first, and these numbers move on the timescale of a
    /// background sweep anyway.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The four readings §10 takes from stored state (§13.44).
/// </summary>
/// <remarks>
/// An interface for the same reason <c>ISnapshotAge</c> is one: a gauge's
/// source should be a value, and taking the whole hosted service to read four
/// numbers makes the metrics unconstructible without a data source, an options
/// binding and a logger.
/// </remarks>
public interface IStateReadings
{
    long LiveReplicas { get; }

    long SilentReplicas { get; }

    long RetiredReplicas { get; }

    long StoredSnapshots { get; }
}

/// <summary>
/// Readings taken from stored state rather than from a line of code.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Row 8's finding, in the general form.</strong> A counter next to a
/// write measures that control reached the line after it. §10's acknowledgement
/// counter read identically on a broken instance that wrote nothing and a
/// healthy one that wrote everything, because the write was deleted and the
/// increment beside it was not. Moving the increment fixes that one case and
/// keeps the class: it is correct against today's code and wrong again the first
/// time a branch, an early return or a retry is inserted between the two.
/// </para><para>
/// <strong>A reading derived from state cannot decouple from what it reports,
/// because it is what it reports.</strong> "How many live replicas have never
/// said what they hold" is answered by the rows themselves. There is no
/// statement to delete.
/// </para><para>
/// <strong>They detect; they do not localise.</strong> Every instance reads the
/// same Postgres, so these gauges are identical everywhere by construction — a
/// frozen frontier shows up on both instances at once and says nothing about
/// which one caused it. That is the honest division of labour: <em>state-derived
/// readings say a thing is wrong, per-instance counters say where</em>, and
/// §10 needs both. A dashboard carrying only the second can be lied to; one
/// carrying only the first cannot answer row 8's second half.
/// </para>
/// </remarks>
public sealed partial class StateReadings : BackgroundService, IStateReadings
{
    // count(*) on document_ops is deliberately not here. It is the one reading
    // that would answer "were the operations actually written", and on the
    // largest table in the schema it is a sequential scan — a gauge that costs
    // a table scan every thirty seconds is a gauge someone turns off. Recorded
    // in docs/section-10-audit.md as derivable-but-expensive rather than
    // silently omitted.
    private const string Replicas = """
        SELECT
            count(*) FILTER (WHERE retired_at IS NULL) AS live,
            count(*) FILTER (WHERE retired_at IS NULL AND acknowledged = '{}'::jsonb) AS silent,
            count(*) FILTER (WHERE retired_at IS NOT NULL) AS retired
        FROM document_replicas;
        """;

    private const string Snapshots = "SELECT count(*) FROM document_snapshots;";

    private readonly NpgsqlDataSource _dataSource;
    private readonly StateReadingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<StateReadings> _logger;

    private long _live;
    private long _silent;
    private long _retired;
    private long _snapshots;

    public StateReadings(
        NpgsqlDataSource dataSource,
        IOptions<StateReadingOptions> options,
        TimeProvider time,
        ILogger<StateReadings> logger)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _dataSource = dataSource;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Replicas that have not been retired.</summary>
    public long LiveReplicas => Interlocked.Read(ref _live);

    /// <summary>
    /// Live replicas whose acknowledged vector is empty.
    /// </summary>
    /// <remarks>
    /// The reading that would have caught row 8's break. A replica that has
    /// connected and never said what it holds pins the stability frontier at
    /// nothing for its document, so collection there can never run — and until
    /// this existed, the only sign was a counter that said the acknowledgement
    /// had arrived.
    /// </remarks>
    public long SilentReplicas => Interlocked.Read(ref _silent);

    /// <summary>Replicas carrying a retirement timestamp.</summary>
    public long RetiredReplicas => Interlocked.Read(ref _retired);

    /// <summary>Snapshot rows stored.</summary>
    public long StoredSnapshots => Interlocked.Read(ref _snapshots);

    /// <summary>Reads once, outside the schedule.</summary>
    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(Replicas, connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _live, reader.GetInt64(0));
                Interlocked.Exchange(ref _silent, reader.GetInt64(1));
                Interlocked.Exchange(ref _retired, reader.GetInt64(2));
            }
        }

        await using var snapshots = new NpgsqlCommand(Snapshots, connection);
        Interlocked.Exchange(
            ref _snapshots,
            (long)(await snapshots.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _time);

        // Once at startup, so an instance that has just come up reports the
        // state rather than zeros for its first interval — zeros which read
        // exactly like a database with nothing in it.
        await SafelyAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            await SafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (NpgsqlException exception)
        {
            Log.ReadFailed(_logger, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3470,
            Level = LogLevel.Warning,
            Message = "A state reading failed; §10's derived gauges are stale.")]
        public static partial void ReadFailed(ILogger logger, Exception exception);
    }
}
