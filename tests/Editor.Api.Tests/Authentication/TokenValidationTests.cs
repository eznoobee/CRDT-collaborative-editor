using System.Text.RegularExpressions;
using Editor.Api.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Editor.Api.Tests.Authentication;

/// <summary>
/// §7's bearer-token rules, including the one that is about absence.
/// </summary>
public sealed partial class TokenValidationTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any())
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir;
    }

    /// <summary>
    /// Matches a validation switch being turned off, in C# or in JSON, however
    /// it is spaced.
    /// </summary>
    [GeneratedRegex(
        @"(ValidateIssuer|ValidateAudience|ValidateLifetime|ValidateIssuerSigningKey|RequireSignedTokens|RequireExpirationTime)""?\s*[=:]\s*false",
        RegexOptions.IgnoreCase)]
    private static partial Regex DisabledValidation();

    [Fact]
    public void No_validation_switch_is_turned_off_anywhere_including_dev_config()
    {
        // §7: "No ValidateIssuer = false anywhere, including in dev config."
        //
        // A test that only checked the options object this build happens to
        // construct would miss the case §7 is actually worried about — a second
        // configuration, added later for a local environment, that quietly
        // relaxes one check. So this reads the source and the settings files,
        // which is where such a thing would live.
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var directory in new[] { "src", "tests" })
        {
            var dir = new DirectoryInfo(Path.Combine(root.FullName, directory));
            foreach (var file in dir.EnumerateFiles("*.*", SearchOption.AllDirectories))
            {
                if (file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Extension is not (".cs" or ".json"))
                {
                    continue;
                }

                // This file names the patterns in order to look for them.
                if (string.Equals(file.Name, "TokenValidationTests.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file.FullName);
                foreach (var match in DisabledValidation().Matches(text).Cast<Match>())
                {
                    var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root.FullName, file.FullName)}:{line}: {match.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "§7 forbids disabling a token validation check anywhere, including dev config:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_check_section_7_names_is_on()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configuration());
        services.AddLogging();
        services.AddEditorAuthentication(Configuration());

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);

        var parameters = options.TokenValidationParameters;

        Assert.True(parameters.ValidateIssuer);
        Assert.True(parameters.ValidateAudience);
        Assert.True(parameters.ValidateLifetime);
        Assert.True(parameters.ValidateIssuerSigningKey);
        Assert.True(parameters.RequireSignedTokens);
        Assert.True(parameters.RequireExpirationTime);

        Assert.Equal("https://issuer.example/", parameters.ValidIssuer);
        Assert.Equal("editor-api", parameters.ValidAudience);
    }

    [Fact]
    public void The_clock_skew_is_zero_by_default()
    {
        // The framework default is five minutes, which accepts a token that
        // expired four minutes ago. §7 asks for lifetime to be validated, and a
        // five-minute grace nobody chose is not that.
        using var provider = Provider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.Zero, options.TokenValidationParameters.ClockSkew);
    }

    [Fact]
    public void Unsigned_and_symmetric_algorithms_are_not_accepted()
    {
        // Not in §7, and it belongs with these: a token signed with "none", or
        // an HMAC token signed with the public key, validates against parameters
        // that do not say which algorithms are acceptable.
        using var provider = Provider();
        var options = provider
            .GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);

        var permitted = options.TokenValidationParameters.ValidAlgorithms;

        Assert.NotNull(permitted);
        Assert.All(permitted, algorithm => Assert.StartsWith("RS", algorithm, StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_configuration_section_fails_validation()
    {
        // The failure mode §7 is guarding against is not someone typing a
        // validation switch off; it is a deployment with no issuer configured
        // that starts anyway and accepts whatever arrives. A missing section
        // and a blank one are the same failure here, and the message has to
        // name what is absent — "authentication failed to configure" sends
        // whoever is on call reading source at 3am.
        var services = new ServiceCollection();
        var empty = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(empty);
        services.AddLogging();
        services.AddEditorAuthentication(empty);

        using var provider = services.BuildServiceProvider();
        var thrown = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OidcOptions>>().Value);

        var message = string.Join("\n", thrown.Failures);
        Assert.Contains(nameof(OidcOptions.Issuer), message, StringComparison.Ordinal);
        Assert.Contains(nameof(OidcOptions.Audience), message, StringComparison.Ordinal);
        Assert.Contains(nameof(OidcOptions.MetadataAddress), message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bearer_scheme_cannot_be_configured_from_a_missing_section()
    {
        // Options validation that only fires when someone asks for OidcOptions
        // is validation the authentication scheme can route around: resolving
        // JwtBearerOptions directly would otherwise hand back a scheme built
        // from blank strings, with an empty ValidIssuer that matches nothing —
        // or, on a future edit, everything.
        var services = new ServiceCollection();
        var empty = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(empty);
        services.AddLogging();
        services.AddEditorAuthentication(empty);

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => provider
                .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                .Get(JwtBearerDefaults.AuthenticationScheme));
    }

    [Fact]
    public void Configuration_is_validated_at_host_start_not_on_first_use()
    {
        // ValidateOnStart is what turns "the first request fails" into "the
        // process does not start". Without it a misconfigured deployment goes
        // green in every health check and fails only for users. The registered
        // validator is the observable difference, so this asserts it exists.
        var services = new ServiceCollection();
        var empty = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(empty);
        services.AddLogging();
        services.AddEditorAuthentication(empty);

        using var provider = services.BuildServiceProvider();
        var validators = provider.GetServices<IStartupValidator>().ToList();

        Assert.NotEmpty(validators);
        Assert.All(validators, validator => Assert.Throws<OptionsValidationException>(validator.Validate));
    }

    [Fact]
    public void A_partial_configuration_fails_validation()
    {
        var partial = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://issuer.example/",
                // No audience and no metadata address.
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(partial);
        services.AddLogging();
        services.AddEditorAuthentication(partial);

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<OidcOptions>>().Value);
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = "https://issuer.example/",
                ["Oidc:Audience"] = "editor-api",
                ["Oidc:MetadataAddress"] = "https://issuer.example/.well-known/openid-configuration",
            })
            .Build();

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        services.AddSingleton(configuration);
        services.AddLogging();
        services.AddEditorAuthentication(configuration);
        return services.BuildServiceProvider();
    }
}
