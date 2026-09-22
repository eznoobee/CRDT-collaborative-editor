using System.Net;
using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §7's client hardening, asserted as a policy rather than as a header's
/// presence.
/// </summary>
/// <remarks>
/// These run against a test host, which is the level that can check the policy
/// is assembled correctly from a given issuer. What a test host cannot show is
/// that the shipped configuration produces it, or that the application still
/// works under it — both are asserted against the Compose stack, and §7's
/// done-when for CSP lives there deliberately (§13.28).
/// </remarks>
public sealed class SecurityHeaderTests
{
    [Fact]
    public void The_policy_denies_by_default_and_grants_each_capability_deliberately()
    {
        var policy = SecurityHeaders.ContentSecurityPolicy("https://issuer.test.invalid/");
        var directives = policy.Split("; ").ToHashSet(StringComparer.Ordinal);

        // §7's table, one assertion per row. "No unsafe-inline" is not one of
        // them: that is a prohibition, and a prohibition is satisfied by
        // default-src *, which is why §7 states the values.
        Assert.Contains("default-src 'none'", directives);
        Assert.Contains("script-src 'self'", directives);
        Assert.Contains("style-src 'self'", directives);
        Assert.Contains("img-src 'self' data:", directives);
        Assert.Contains("form-action 'self'", directives);
        Assert.Contains("frame-ancestors 'none'", directives);
        Assert.Contains("base-uri 'none'", directives);
        Assert.Contains("object-src 'none'", directives);
    }

    [Fact]
    public void Connect_src_names_the_configured_issuer_and_not_a_wildcard()
    {
        // The load-bearing directive and the one most likely to break
        // silently: the browser's token exchange, metadata and JWKS fetches
        // all go to the issuer, and a policy without it fails only in a
        // browser, only after a real sign-in.
        var policy = SecurityHeaders.ContentSecurityPolicy("https://issuer.test.invalid:8443/realms/x");

        Assert.Contains("connect-src 'self' https://issuer.test.invalid:8443", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("*", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_issuers_path_is_dropped_because_a_csp_source_is_an_origin()
    {
        // A path on a CSP source is ignored by some browsers and rejected by
        // others, so the origin is what goes in.
        var policy = SecurityHeaders.ContentSecurityPolicy("https://issuer.test.invalid/realms/editor");

        Assert.Contains("connect-src 'self' https://issuer.test.invalid;", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("realms", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unparseable_issuer_narrows_the_policy_rather_than_widening_it()
    {
        // The failure direction matters. A policy that fell back to a wildcard
        // when it could not read the issuer would turn a configuration mistake
        // into an open policy; falling back to 'self' breaks sign-in loudly
        // instead, which is the failure someone notices (§13.29).
        var policy = SecurityHeaders.ContentSecurityPolicy("not-a-url");

        Assert.Contains("connect-src 'self';", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void Hsts_carries_the_configured_value()
    {
        // What is under test is not the browser's HSTS implementation. It is
        // that the header carries the value this deployment was configured
        // with — which a short value proves as well as a long one.
        var shipped = SecurityHeaders.Hsts(new SecurityHeaderOptions { HstsMaxAge = TimeSpan.FromMinutes(1) });
        var production = SecurityHeaders.Hsts(new SecurityHeaderOptions());

        Assert.Equal("max-age=60; includeSubDomains", shipped);
        Assert.Equal("max-age=31536000; includeSubDomains", production);
    }

    [Fact]
    public void The_default_is_the_strict_value_so_a_forgotten_setting_fails_safe()
    {
        Assert.Equal(SecurityHeaderOptions.ProductionHstsMaxAge, new SecurityHeaderOptions().HstsMaxAge);
        Assert.Equal(TimeSpan.FromDays(365), SecurityHeaderOptions.ProductionHstsMaxAge);
    }

    [Fact]
    public async Task Every_response_carries_the_policy_and_nosniff()
    {
        // Including one nobody wrote an endpoint for. A control applied to the
        // endpoints someone remembered is a control that does not apply.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        using var missing = await client.GetAsync(new Uri("/no-such-path-exists", UriKind.Relative), TestContext.Current.CancellationToken);

        foreach (var response in new[] { live, missing })
        {
            Assert.True(
                response.Headers.Contains("Content-Security-Policy"),
                $"{response.RequestMessage?.RequestUri} carried no policy");
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_policy_is_enforced_rather_than_report_only()
    {
        // The two are indistinguishable in a header dump and one of them
        // enforces nothing.
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.False(response.Headers.Contains("Content-Security-Policy-Report-Only"));
    }
}
