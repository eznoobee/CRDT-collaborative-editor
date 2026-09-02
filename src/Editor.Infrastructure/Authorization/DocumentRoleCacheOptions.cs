using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Authorization;

/// <summary>How stale a cached role may be (PROJECT_SPEC.md §7).</summary>
public sealed class DocumentRoleCacheOptions
{
    public const string Section = "DocumentRoleCache";

    /// <summary>§7's bound on how long revocation may take.</summary>
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a role may be served from cache without re-reading Postgres.
    /// </summary>
    /// <remarks>
    /// §7 makes bounded staleness the requirement rather than freshness: an
    /// uncached lookup per operation is a database round trip per keystroke per
    /// connection, which §8 rules out. Five seconds is the bound, and
    /// invalidation makes the usual case immediate.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:00.001", "00:00:05")]
    public TimeSpan Ttl { get; set; } = MaximumTtl;

    /// <summary>Redis key prefix for cached roles.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; set; } = "document-role:";

    /// <summary>Redis pub/sub channel carrying eager invalidations.</summary>
    [Required(AllowEmptyStrings = false)]
    public string InvalidationChannel { get; set; } = "document-role-invalidation";
}
