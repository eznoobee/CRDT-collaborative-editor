using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Ingest;

/// <summary>
/// §7's abuse limits on operation submission, in code points per interval.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The unit is the requirement.</strong> §7 says code points rather
/// than messages so that a run operation cannot buy 256 characters for the
/// price of one, which means the budget is charged the batch's expanded
/// operation count — runs are expanded on ingest (3.6), so one 256-code-point
/// run and 256 single-character inserts cost exactly the same. A limiter
/// counting messages satisfies every test written with single-character
/// operations and none of §7.
/// </para><para>
/// <strong>The default is set by pasting, not by typing.</strong> A fast typist
/// sustains around ten code points a second, so any number here is orders of
/// magnitude above typing and typing is not what sets it. What sets it is the
/// largest ordinary burst: a paste arrives as one batch per 256 operations,
/// drained back to back, so pasting three pages of prose is three thousand code
/// points in about as long as the round trips take. A thousand per ten seconds
/// — the first number written here — refused that, which is a limiter that
/// breaks the application before it inconveniences an attacker.
/// </para><para>
/// Ten thousand per connection per ten seconds leaves a three-page paste
/// untouched and still bounds one connection to a thousand rows a second in the
/// operation log. A paste larger than the budget is throttled rather than
/// refused: §9's recovery for <c>rate_limited</c> resubmits the same batch when
/// the window rolls over, so it completes across several windows. That is the
/// limit working, and it is the reason the refusal has to carry a delay.
/// </para>
/// </remarks>
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>The window each budget is counted over.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Code points one user may submit per interval, across every connection they hold.</summary>
    [Range(1, 10_000_000)]
    public int CodePointsPerUser { get; set; } = 30_000;

    /// <summary>Code points one connection may submit per interval.</summary>
    /// <remarks>
    /// Not redundant with the per-user budget: a single runaway tab is the
    /// common case and is stopped here without penalising the same person's
    /// other sessions, while the per-user budget is what a fleet of tabs runs
    /// into.
    /// </remarks>
    [Range(1, 10_000_000)]
    public int CodePointsPerConnection { get; set; } = 10_000;

    /// <summary>Redis key prefix for the counters.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; set; } = "rate:";
}
