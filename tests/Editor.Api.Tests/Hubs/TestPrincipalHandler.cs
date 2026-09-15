using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Stands in for the OIDC provider, so the authorization tests can be about
/// authorization.
/// </summary>
/// <remarks>
/// This replaces token validation, which is exactly the kind of substitution
/// that can hide a real hole — a negotiate endpoint that forgot
/// RequireAuthorization would pass every test in this file. That gap is closed
/// separately: NegotiateTests drives the unmodified host and asserts an
/// anonymous call is refused, and §7's validation rules have their own tests
/// against the real handler in TokenValidationTests.
/// <para>
/// It reads the identity from headers rather than minting a JWT because a test
/// signing key is one more credential in the repository, and §7 would rather
/// there were none.
/// </para>
/// </remarks>
public sealed class TestPrincipalHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string IssuerHeader = "X-Test-Issuer";
    public const string SubjectHeader = "X-Test-Subject";

    public TestPrincipalHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var issuer = Request.Headers[IssuerHeader].ToString();
        var subject = Request.Headers[SubjectHeader].ToString();

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subject))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim("iss", issuer), new Claim("sub", subject)], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
