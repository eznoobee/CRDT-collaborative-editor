using System.ComponentModel.DataAnnotations;
using Editor.Api.Authentication;
using Microsoft.Extensions.Options;

namespace Editor.Api.Infrastructure;

/// <summary>§7's client hardening, as configuration.</summary>
public sealed class SecurityHeaderOptions
{
    public const string Section = "SecurityHeaders";

    /// <summary>§7's production value: one year.</summary>
    public static readonly TimeSpan ProductionHstsMaxAge = TimeSpan.FromDays(365);

    /// <summary>
    /// How long a browser is told to refuse plaintext for this host.
    /// </summary>
    /// <remarks>
    /// Defaults to §7's production value, and this Compose stack overrides it
    /// to sixty seconds. The divergence is deliberate, named in §7, and carries
    /// its own row in the §7 requirement map: a long <c>max-age</c> served once
    /// from a development host pins that browser profile to HTTPS for a year
    /// and nothing in this application can undo it.
    /// <para>
    /// The default is the strict value rather than the lenient one on purpose.
    /// A deployment that sets nothing gets the setting that protects it; only a
    /// deployment that deliberately says otherwise gets the short one, which is
    /// the direction a forgotten configuration should fail in.
    /// </para>
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "3650.00:00:00")]
    public TimeSpan HstsMaxAge { get; set; } = ProductionHstsMaxAge;

    /// <summary>Whether the HSTS header claims subdomains too.</summary>
    public bool HstsIncludeSubDomains { get; set; } = true;
}

/// <summary>
/// The headers §7 requires on every response (PROJECT_SPEC.md §7).
/// </summary>
/// <remarks>
/// <para>
/// Middleware rather than per-endpoint, and registered before routing, so that
/// every response carries them: the API's JSON, the SPA's <c>index.html</c>
/// served by the fallback, the static assets, and the error pages nobody
/// writes on purpose. A control applied to the endpoints someone remembered is
/// 5b.4's shape — a control that exists and does not apply.
/// </para><para>
/// <strong>The policy is assembled from configuration, and that is the part
/// most likely to break silently.</strong> §7's <c>connect-src</c> has to name
/// the configured issuer's origin, because the browser's token exchange,
/// metadata and JWKS fetches all go there. A policy built without it passes
/// every header assertion and every API test and fails only in a browser,
/// only after a real sign-in — which is why §7's done-when is zero violations
/// on a page that has actually signed in, loaded a document and opened its
/// socket.
/// </para><para>
/// <strong>HSTS is emitted only over HTTPS</strong>, per the standard: the
/// header is meaningless on a plaintext response and browsers ignore it there.
/// That makes it a load-bearing observation rather than a formality, because
/// whether this request looks like HTTPS depends on <c>ForwardedHeaders</c>
/// being configured for the proxy that actually fronts the stack — the one
/// §7-relevant Compose setting with a default rather than a required value.
/// Its misconfiguration was previously unobservable; now it removes a header a
/// test asserts.
/// </para>
/// </remarks>
public static class SecurityHeaders
{
    /// <summary>Registers §7's header options.</summary>
    public static IServiceCollection AddSecurityHeaders(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SecurityHeaderOptions>()
            .Bind(configuration.GetSection(SecurityHeaderOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>Adds §7's headers to every response.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var headers = app.ApplicationServices.GetRequiredService<IOptions<SecurityHeaderOptions>>().Value;
        var oidc = app.ApplicationServices.GetRequiredService<IOptions<OidcOptions>>().Value;
        var policy = ContentSecurityPolicy(oidc.Issuer);
        var hsts = Hsts(headers);

        return app.Use(async (context, next) =>
        {
            var response = context.Response;

            // Set before the response starts, because a header added after the
            // first byte is written is a header nobody receives.
            response.OnStarting(() =>
            {
                response.Headers["Content-Security-Policy"] = policy;
                response.Headers["X-Content-Type-Options"] = "nosniff";

                if (context.Request.IsHttps)
                {
                    response.Headers["Strict-Transport-Security"] = hsts;
                }

                return Task.CompletedTask;
            });

            await next().ConfigureAwait(false);
        });
    }

    /// <summary>§7's policy, with the configured issuer in `connect-src`.</summary>
    /// <remarks>
    /// Written out directive by directive because §7 states it that way: "no
    /// <c>unsafe-inline</c>" is a prohibition and is satisfied by
    /// <c>default-src *</c>, so the requirement is the value rather than the
    /// absence.
    /// </remarks>
    public static string ContentSecurityPolicy(string issuer)
    {
        // The origin, not the whole issuer URL: a CSP source is scheme, host
        // and port, and a path on the end is ignored by some browsers and
        // rejected by others.
        var origin = Uri.TryCreate(issuer, UriKind.Absolute, out var parsed)
            ? parsed.GetLeftPart(UriPartial.Authority)
            : string.Empty;

        var connect = origin.Length == 0 ? "'self'" : $"'self' {origin}";

        return string.Join("; ",
        [
            "default-src 'none'",
            "script-src 'self'",
            "style-src 'self'",
            $"connect-src {connect}",
            "img-src 'self' data:",
            "font-src 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'",
            "base-uri 'none'",
            "object-src 'none'",
        ]);
    }

    /// <summary>The HSTS header value for these options.</summary>
    public static string Hsts(SecurityHeaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var seconds = (long)options.HstsMaxAge.TotalSeconds;
        return options.HstsIncludeSubDomains
            ? $"max-age={seconds}; includeSubDomains"
            : $"max-age={seconds}";
    }
}
