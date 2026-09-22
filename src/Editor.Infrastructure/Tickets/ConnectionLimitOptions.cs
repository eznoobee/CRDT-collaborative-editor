using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Tickets;

/// <summary>§7's per-user connection cap.</summary>
/// <remarks>
/// <para>
/// <strong>Distinct from the per-document replica cap, which already exists.</strong>
/// The two are observationally identical against one user on one document —
/// both refuse the next connection — so only a test that opens connections
/// across <em>different</em> documents can tell them apart (§13.31).
/// </para><para>
/// <strong>The number is set by a person's plausible fan-out, not by abuse.</strong>
/// §13.37: a limit reasoned about from the activity it is named after lands in
/// the wrong place. Someone with a dozen documents open in a dozen tabs is
/// doing ordinary work, and a browser that reconnects on wake produces a burst
/// on top of it. Thirty-two leaves that untouched while bounding what one
/// account can pin open across the fleet.
/// </para><para>
/// <strong><see cref="StaleAfter"/> is what stops a dead instance locking a
/// user out.</strong> A slot is held by an entry that has to keep saying it is
/// still there; an instance that dies holding fifty of them leaves entries that
/// age out rather than a user who cannot reconnect until an operator
/// intervenes. Same bound, same reasoning and the same failure mode as the
/// replica claims (§7).
/// </para>
/// </remarks>
public sealed class ConnectionLimitOptions
{
    public const string Section = "ConnectionLimits";

    /// <summary>Live connections one user may hold, across every document.</summary>
    [Range(1, 10_000)]
    public int MaxPerUser { get; set; } = 32;

    /// <summary>
    /// How long a slot survives without renewal before it is treated as gone.
    /// </summary>
    /// <remarks>
    /// Must be comfortably longer than the renewal interval, and the
    /// registration checks that rather than trusting it: a stale window shorter
    /// than the renewal period would expire live connections' slots between
    /// ticks, and the symptom would be users randomly refused while well under
    /// the cap.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Redis key prefix for the per-user connection sets.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; set; } = "conn:";
}
