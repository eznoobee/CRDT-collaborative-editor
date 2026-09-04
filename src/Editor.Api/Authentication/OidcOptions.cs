using System.ComponentModel.DataAnnotations;

namespace Editor.Api.Authentication;

/// <summary>
/// What §7 requires validating on every bearer token.
/// </summary>
/// <remarks>
/// There is no default issuer or audience and no way to disable either check.
/// §7 forbids switching any token validation off anywhere, dev config included,
/// and the way to keep that true is to leave nowhere to put it: a missing issuer
/// fails startup rather than falling back to accepting everything.
/// <para>
/// The phrase §7 uses is deliberately not quoted here. A source scanner in
/// <c>TokenValidationTests</c> greps every .cs and .json file for a validation
/// switch being assigned false, and prose that quotes the forbidden line is
/// indistinguishable from the line itself. Say it in words instead.
/// </para>
/// </remarks>
public sealed class OidcOptions
{
    public const string Section = "Oidc";

    /// <summary>The `iss` every token must carry.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The `aud` every token must carry.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Where the signing keys come from.</summary>
    [Required(AllowEmptyStrings = false)]
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>
    /// The public client id the browser authenticates as (§7's PKCE flow).
    /// </summary>
    /// <remarks>
    /// Optional, and deliberately not a secret: a browser cannot keep one, so
    /// §7 requires Authorization Code with PKCE and no client secret. An API
    /// deployed without a single-page application never needs this, which is
    /// why its absence is not a startup failure — but a client that asks for
    /// configuration and receives none must refuse to start a login rather than
    /// invent an id, so this reaches the browser as an empty value it checks.
    /// </remarks>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Leeway on `exp` and `nbf`. Zero by default, which is the honest setting:
    /// the framework's five-minute default silently accepts tokens that expired
    /// five minutes ago, and §7 asks for lifetime to be validated.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.Zero;
}
