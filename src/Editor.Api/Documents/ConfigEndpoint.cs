using Editor.Api.Authentication;
using Microsoft.Extensions.Options;

namespace Editor.Api.Documents;

/// <summary>
/// What the browser needs before it can authenticate (PROJECT_SPEC.md §7, §9).
/// </summary>
/// <remarks>
/// Served rather than baked into the bundle. A build that carries its issuer is
/// a build per environment, and the first time one is promoted from staging to
/// production it authenticates against the wrong identity provider — which
/// looks like a login bug rather than a deployment one.
/// <para>
/// Anonymous, necessarily: this is what a client reads in order to log in. Both
/// values are public by construction — the issuer and the client id travel in
/// the authorization request URL, in the browser's address bar, on every login.
/// Nothing else belongs here, and §7's no-secret rule is what keeps it that way.
/// </para>
/// </remarks>
public static class ConfigEndpoint
{
    /// <param name="Issuer">The OIDC issuer the browser authenticates against.</param>
    /// <param name="ClientId">
    /// Empty when no single-page application is deployed. Reported as empty
    /// rather than omitted, so a misconfigured client fails saying what is
    /// missing instead of reading undefined and starting a login it cannot
    /// finish.
    /// </param>
    public sealed record ClientConfiguration(string Issuer, string ClientId);

    public static void MapClientConfiguration(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/config", (IOptions<OidcOptions> options) =>
            Results.Ok(new ClientConfiguration(options.Value.Issuer, options.Value.ClientId)));
    }
}
