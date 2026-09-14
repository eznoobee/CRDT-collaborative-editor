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

    /// <summary>
    /// How recently a replica must have been seen to count as active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without a window, "silent replicas" is a permanently red
    /// signal.</strong> A replica stays live until <c>T_retire</c> — seven days
    /// — so every abandoned session accumulates in the count, and an absolute
    /// total is dominated by history rather than by anything happening now. The
    /// first run of this gauge read 215 silent of 234 live, almost all of it
    /// sessions from previous days.
    /// </para><para>
    /// That is row 8's first finding arriving in the fix for its second: a
    /// signal that is always red has no diagnostic value, whatever it measures.
    /// The window makes it a reading about the present — a replica seen in the
    /// last few minutes that has never said what it holds is an anomaly; the
    /// same replica a week later is waiting for retirement.
    /// </para><para>
    /// Fifteen minutes: three times §5's five-minute connection heartbeat, so a
    /// live connection is always inside it and a closed one leaves within three
    /// missed beats.
    /// </para>
    /// </remarks>
    public TimeSpan ActiveWindow { get; set; } = TimeSpan.FromMinutes(15);
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

    long ActiveReplicas { get; }

    long SilentReplicas { get; }

    long RetiredReplicas { get; }

    long StoredSnapshots { get; }

    /// <summary>
    /// How long ago these were read, in seconds.
    /// </summary>
    /// <remarks>
    /// <strong>Part of the reading, not a detail.</strong> These are refreshed
    /// on a schedule rather than per scrape, so every one of them is up to one
    /// interval old — and a gauge that reports the past as the present is the
    /// same class of problem as a counter that reports a write that did not
    /// happen. The first run after the window was added read zero live replicas
    /// while twelve existed, because the only reading so far had been taken at
    /// startup against an empty database. Nothing about the numbers said so.
    /// </remarks>
    double ReadingAgeSeconds { get; }
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
            count(*) FILTER (WHERE retired_at IS NULL AND last_seen_at > $1) AS active,
            count(*) FILTER (
                WHERE retired_at IS NULL
                  AND last_seen_at > $1
                  AND acknowledged = '{}'::jsonb) AS silent,
            count(*) FILTER (WHERE retired_at IS NOT NULL) AS retired
        FROM document_replicas;
        """;

    private const string Snapshots = "SELECT count(*) FROM document_snapshots;";

    private readonly NpgsqlDataSource _dataSource;
    private readonly StateReadingOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<StateReadings> _logger;

    private long _live;
    private long _active;
    private long _silent;
    private long _retired;
    private long _snapshots;
    private long _readAtUnixMs;

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
    /// Live replicas seen within <see cref="StateReadingOptions.ActiveWindow"/>.
    /// </summary>
    /// <remarks>
    /// The denominator <see cref="SilentReplicas"/> needs. "Six silent" means
    /// nothing without it; "six silent of six active" is a diagnosis.
    /// </remarks>
    public long ActiveReplicas => Interlocked.Read(ref _active);

    /// <summary>
    /// Recently active replicas whose acknowledged vector is empty.
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

    /// <summary>How long ago the readings above were taken.</summary>
    public double ReadingAgeSeconds
    {
        get
        {
            var readAt = Interlocked.Read(ref _readAtUnixMs);

            // Negative would be meaningless and -1 is not a duration, so a
            // reading that has never happened reports its age as the process's
            // whole uptime: old enough that nobody mistakes it for current.
            return readAt == 0
                ? _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0
                : (_time.GetUtcNow().ToUnixTimeMilliseconds() - readAt) / 1000.0;
        }
    }

    /// <summary>Reads once, outside the schedule.</summary>
    public async Task ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var command = new NpgsqlCommand(Replicas, connection))
        {
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = _time.GetUtcNow() - _options.ActiveWindow,
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _live, reader.GetInt64(0));
                Interlocked.Exchange(ref _active, reader.GetInt64(1));
                Interlocked.Exchange(ref _silent, reader.GetInt64(2));
                Interlocked.Exchange(ref _retired, reader.GetInt64(3));
            }
        }

        await using var snapshots = new NpgsqlCommand(Snapshots, connection);
        Interlocked.Exchange(
            ref _snapshots,
            (long)(await snapshots.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L));

        Interlocked.Exchange(ref _readAtUnixMs, _time.GetUtcNow().ToUnixTimeMilliseconds());
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
