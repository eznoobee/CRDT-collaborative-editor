using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>When a replica stops counting toward causal stability (§5).</summary>
public sealed class ReplicaRetirementOptions
{
    public const string Section = "ReplicaRetirement";

    /// <summary>
    /// <c>T_retire</c>: how long a replica may be inactive before it is retired.
    /// </summary>
    /// <remarks>
    /// §5 fixes this at seven days, and the number is not a tuning knob in the
    /// way the rate limits are. It bounds two things in opposite directions.
    /// Too long and causal stability never converges, so GC reclaims nothing and
    /// documents grow without bound. <strong>Too short and a replica is retired
    /// while its owner still holds unsent work</strong>, which §9's offline
    /// window exists to warn about and which is data loss rather than an
    /// inconvenience — so of the two failures, this is the one to stay far away
    /// from. Seven days covers a long weekend, a holiday, and a laptop that
    /// stayed shut for a week.
    /// </remarks>
    public TimeSpan Retire { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the job looks for replicas to retire.</summary>
    /// <remarks>
    /// Hourly. Retirement is a seven-day threshold, so nothing is gained by
    /// checking often, and this runs a write against every document's replica
    /// table on a shared database.
    /// </remarks>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How often a live connection's <c>last_seen_at</c> is refreshed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without this, retirement retires live readers.</strong>
    /// <c>last_seen_at</c> is written by <c>negotiate</c> and by nothing else,
    /// so a person who opens a document and reads it for longer than
    /// <see cref="Retire"/> has a replica that looks untouched for a week while
    /// their socket is open. Retiring it advances the stability frontier past
    /// state that replica still holds, which is the one thing §5 says must not
    /// happen.
    /// </para><para>
    /// The heartbeat has to be attached to the <em>connection</em> rather than
    /// to submission, for the same reason §7's revocation sweep does: a viewer
    /// submits nothing, and an activity signal derived from writing does not
    /// cover principals who only read (§13.32).
    /// </para>
    /// </remarks>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Sets <c>retired_at</c> on replicas inactive past <c>T_retire</c> (§5).
/// </summary>
/// <remarks>
/// <para>
/// Register row 1, and the thing every other part of Phase 7 waits on. Causal
/// stability over an open-ended replica set never converges: one browser tab
/// that never returns holds the frontier still forever, so GC reclaims nothing.
/// Retirement is what removes it from the set.
/// </para><para>
/// <strong><see cref="Retired"/> is not optional instrumentation.</strong> A
/// retirement job that silently never runs leaves the frontier where it is, and
/// a frontier that never advances passes every correctness test there is —
/// convergence still holds, no document is wrong, nothing is red. Two silent
/// failures compose into a green suite over a GC that does nothing, and the
/// count is the only thing that separates "ran and found nothing to retire"
/// from "did not run" (§13.15).
/// </para>
/// </remarks>
public sealed partial class ReplicaRetirement : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReplicaRetirementOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ReplicaRetirement> _logger;
    private long _retired;
    private long _sweeps;

    public ReplicaRetirement(
        IServiceScopeFactory scopes,
        IOptions<ReplicaRetirementOptions> options,
        TimeProvider time,
        ILogger<ReplicaRetirement> logger)
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

    /// <summary>Replicas this instance has retired since it started.</summary>
    public long Retired => Interlocked.Read(ref _retired);

    /// <summary>Sweeps completed, whether or not they retired anything.</summary>
    /// <remarks>
    /// The pair to <see cref="Retired"/>. Zero retirements with zero sweeps is a
    /// job that is not running; zero retirements with many sweeps is a job that
    /// is running and has nothing to do. Only one of those is fine, and a single
    /// counter cannot tell them apart.
    /// </remarks>
    public long Sweeps => Interlocked.Read(ref _sweeps);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.Interval, _time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await RetireAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Never allowed to end the loop: a retirement service that
                // stopped on one bad tick would stop GC across the fleet, and
                // nothing downstream would report anything but a lack of
                // reclamation.
                Log.SweepFailed(_logger, exception);
            }
        }
    }

    /// <summary>Retires everything past <c>T_retire</c>; answers how many.</summary>
    /// <remarks>
    /// Public so a test can drive one sweep against a controlled clock rather
    /// than waiting an hour for a timer — the sweep is the unit under test, and
    /// the timer is <see cref="PeriodicTimer"/>'s business.
    /// </remarks>
    public async Task<int> RetireAsync(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow() - _options.Retire;

        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        // One statement, in the database. Loading candidates and saving them
        // back would race every other instance running the same sweep, and
        // there is no reason for the rows to travel.
        var retired = await context.DocumentReplicas
            .Where(replica => replica.RetiredAt == null && replica.LastSeenAt < cutoff)
            .ExecuteUpdateAsync(
                update => update.SetProperty(replica => replica.RetiredAt, _time.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);

        Interlocked.Increment(ref _sweeps);

        if (retired > 0)
        {
            Interlocked.Add(ref _retired, retired);
            Log.Retired(_logger, retired, cutoff);
        }

        return retired;
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3430,
            Level = LogLevel.Information,
            Message = "Retired {Count} replicas inactive since {Cutoff}.")]
        public static partial void Retired(ILogger logger, int count, DateTimeOffset cutoff);

        [LoggerMessage(
            EventId = 3431,
            Level = LogLevel.Error,
            Message = "A replica retirement sweep failed.")]
        public static partial void SweepFailed(ILogger logger, Exception exception);
    }
}
