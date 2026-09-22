using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Editor.Api.Authentication;

/// <summary>Bearer authentication as PROJECT_SPEC.md §7 specifies it.</summary>
public static class AuthenticationExtensions
{
    /// <summary>Registers OIDC bearer validation with every §7 check on.</summary>
    /// <remarks>
    /// Nothing here reads configuration eagerly. Registration runs before the
    /// host has finished assembling its configuration sources — under
    /// <c>WebApplicationFactory</c>, and under any deployment that adds a
    /// secrets provider after this call — so a value read at registration is a
    /// value read too early. The checks are attached to the options pipeline
    /// instead and run at host start, which is still before the first request:
    /// a process with no issuer configured does not reach a listening state.
    /// </remarks>
    public static IServiceCollection AddEditorAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // A missing section and an empty one are the same failure: every field
        // is [Required], so both produce a validation error naming what is
        // absent. There is no branch where an unconfigured issuer yields a
        // working authentication scheme.
        services.AddOptions<OidcOptions>()
            .Bind(configuration.GetSection(OidcOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<OidcOptions>>((options, monitor) =>
            {
                // CurrentValue runs the data-annotation validation, so this
                // throws rather than configuring a scheme from blank strings.
                var oidc = monitor.CurrentValue;

                options.MetadataAddress = oidc.MetadataAddress;
                options.Authority = oidc.Issuer;
                options.Audience = oidc.Audience;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    // §7's four checks, written out rather than left to the
                    // defaults, so that reading this file answers the question
                    // rather than sending you to the framework's.
                    ValidateIssuer = true,
                    ValidIssuer = oidc.Issuer,
                    ValidateAudience = true,
                    ValidAudience = oidc.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    ClockSkew = oidc.ClockSkew,

                    // The algorithm list is not in §7 and belongs here anyway: a
                    // token signed with "none", or an HMAC token signed with the
                    // public key, validates fine against a parameter set that
                    // does not say which algorithms are acceptable.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512],
                    RequireSignedTokens = true,
                    RequireExpirationTime = true,
                };
            });

        services.AddAuthorization();
        return services;
    }
}
