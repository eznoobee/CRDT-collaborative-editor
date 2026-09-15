using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Tickets;

/// <summary>How long a connect ticket lives, and where it is kept.</summary>
public sealed class ConnectTicketOptions
{
    public const string Section = "ConnectTicket";

    /// <summary>The ceiling §7 sets on ticket lifetime.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long an issued ticket remains redeemable. §7 says at most 60
    /// seconds, and the ticket travels in a URL — reverse-proxy access logs,
    /// browser history, <c>Referer</c> headers — so the window is the whole of
    /// the mitigation.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:01:00")]
    public TimeSpan Lifetime { get; set; } = MaximumLifetime;

    /// <summary>
    /// Redis key prefix. Configurable so that two environments sharing one
    /// Redis cannot redeem each other's tickets.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; set; } = "connect-ticket:";
}
