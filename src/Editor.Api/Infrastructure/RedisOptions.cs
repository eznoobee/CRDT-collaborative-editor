using System.ComponentModel.DataAnnotations;

namespace Editor.Api.Infrastructure;

/// <summary>How to reach the Redis that holds tickets, role cache and backplane.</summary>
public sealed class RedisOptions
{
    public const string Section = "Redis";

    /// <summary>
    /// A StackExchange.Redis configuration string.
    /// </summary>
    /// <remarks>
    /// No default, for the reason §7 gives about issuers: a fallback of
    /// <c>localhost</c> turns a misconfigured deployment into one that starts,
    /// finds nothing, and issues tickets nobody can redeem.
    /// </remarks>
    [Required(AllowEmptyStrings = false)]
    public string Configuration { get; set; } = string.Empty;
}
