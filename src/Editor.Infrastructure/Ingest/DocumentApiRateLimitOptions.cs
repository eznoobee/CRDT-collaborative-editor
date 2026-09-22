using System.ComponentModel.DataAnnotations;

namespace Editor.Infrastructure.Ingest;

/// <summary>§7's rate limits on the document API, in requests per interval.</summary>
/// <remarks>
/// <para>
/// <strong>Units are requests, not code points, and that is the whole
/// difference from the submission limits.</strong> §7 says so because a REST
/// call has no natural size: a <c>POST /documents</c> is a row and a title, and
/// there is nothing to weigh it by. The window and the storage are shared with
/// the submission limits; only the unit changes.
/// </para><para>
/// <strong>Writes only, and reads deliberately not.</strong> §7's abuse list
/// names creating documents and granting or revoking membership — the calls
/// that write. A read loop against <c>GET /documents</c> is a load question
/// rather than an abuse question, and it is bounded by the connection cap and
/// by the proxy; adding a number here for it would be inventing a requirement.
/// Stated so that the absence is a decision rather than an oversight, and the
/// filter charges by method so extending it later is one line.
/// </para><para>
/// <strong>The number is set by setting up a document, not by creating
/// one</strong> (§13.37). Creating documents is a slow human act; granting is
/// not — someone sharing a new document with a team of thirty makes thirty
/// calls as fast as they can click, and a limit reasoned about from "how often
/// does someone make a document" would refuse it. A hundred and twenty writes a
/// minute leaves that untouched and still bounds an unattended loop to two
/// rows a second.
/// </para>
/// </remarks>
public sealed class DocumentApiRateLimitOptions
{
    public const string Section = "DocumentApiRateLimits";

    /// <summary>The window requests are counted over.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Writing requests one user may make per interval.</summary>
    [Range(1, 1_000_000)]
    public int WritesPerUser { get; set; } = 120;

    /// <summary>Redis key prefix for the counters.</summary>
    [Required(AllowEmptyStrings = false)]
    public string KeyPrefix { get; set; } = "apirate:";
}
