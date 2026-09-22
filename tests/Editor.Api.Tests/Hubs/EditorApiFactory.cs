using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>The real host, pointed at the fixture's Postgres and Redis.</summary>
public sealed class EditorApiFactory : WebApplicationFactory<Program>
{
    private readonly EditorFixture _fixture;
    private readonly bool _testAuthentication;

    private readonly Dictionary<string, string?>? _settings;

    private readonly Action<IServiceCollection>? _configure;

    public EditorApiFactory(
        EditorFixture fixture,
        bool testAuthentication = true,
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null)
    {
        _fixture = fixture;
        _testAuthentication = testAuthentication;
        _settings = settings;
        _configure = configure;
    }

    /// <summary>How many role lookups the hub has made.</summary>
    /// <remarks>
    /// §7 splits authorization into two checks precisely because they cost
    /// different amounts, and the cheap one exists to stop the expensive one
    /// running. Counting is the only way to tell that apart: both orders give
    /// the same answer.
    /// </remarks>
    public CountingDocumentRoles Roles { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oidc:Issuer"] = ApiFactory.Issuer,
                ["Oidc:Audience"] = ApiFactory.Audience,
                ["Oidc:MetadataAddress"] = ApiFactory.Issuer + ".well-known/openid-configuration",
                ["Redis:Configuration"] = _fixture.Redis.Configuration,
                ["Postgres:ConnectionString"] = _fixture.Postgres.ConnectionString,
            }));

        if (_settings is not null)
        {
            // Applied second, so a test can tighten a cap the way a deployment
            // would — through configuration, not by reaching past it.
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(_settings));
        }

        if (_configure is not null)
        {
            // Applied before the authentication overrides below so a test can
            // replace a service the host registered — the clock, in practice.
            // Configuration is how a deployment changes a number; this is for
            // the things a deployment cannot change and a test must, and it is
            // deliberately the narrow door rather than a general one.
            builder.ConfigureServices(_configure);
        }

        if (!_testAuthentication)
        {
            return;
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IDocumentRoles>(provider =>
            {
                Roles.Inner = provider.GetRequiredService<CachedDocumentRoles>();
                return Roles;
            });

            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestPrincipalHandler>(
                    TestPrincipalHandler.SchemeName, _ => { });

            // Configured after the app's own AddAuthentication, so this wins.
            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestPrincipalHandler.SchemeName;
                options.DefaultChallengeScheme = TestPrincipalHandler.SchemeName;
            });
        });
    }

    /// <summary>A client carrying the identity of one OIDC subject.</summary>
    public HttpClient ClientFor(string subject, string? issuer = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestPrincipalHandler.IssuerHeader, issuer ?? ApiFactory.Issuer);
        client.DefaultRequestHeaders.Add(TestPrincipalHandler.SubjectHeader, subject);
        return client;
    }

    /// <summary>
    /// Creates a document through the product's own API (§12, register row 25).
    /// </summary>
    /// <param name="ownerSubject">Who creates it. A subject, not a user id,
    /// because <c>POST /documents</c> takes an identity and not a row.</param>
    /// <param name="deleted">
    /// Remove it afterwards, through <c>DELETE /documents/{id}</c>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <strong>This used to insert a row.</strong> Register rows 15 and 16 were
    /// exactly that — every harness seeded past the product because nothing in
    /// the product could make a document — and the grep §12 added to stop it
    /// covered <c>client/src</c> only, so this file went on doing it for another
    /// two phases. Row 25 is the scope of a rule being half of it.
    /// </para><para>
    /// It matters beyond tidiness: a seeded document has no owner membership
    /// row unless the harness remembers to add one, no rate-limit charge, and
    /// no <c>updated_at</c> the product wrote — so every test built on it is a
    /// test of a state the product never produces.
    /// </para><para>
    /// "Deleted" goes through <c>DELETE</c> for the same reason. Setting
    /// <c>deleted_at</c> by hand skips the cache invalidation §7 requires, which
    /// is exactly the half 7.6 found already worked and the half it found did
    /// not.
    /// </para>
    /// </remarks>
    public async Task<Guid> CreateDocumentAsync(
        string ownerSubject, bool deleted = false, CancellationToken cancellationToken = default)
    {
        using var client = ClientFor(ownerSubject);

        using var created = await client.PostAsJsonAsync(
            new Uri("/documents", UriKind.Relative),
            new { title = "test" },
            cancellationToken);

        created.EnsureSuccessStatusCode();

        var summary = await created.Content.ReadFromJsonAsync<DocumentSummary>(cancellationToken)
            ?? throw new InvalidOperationException("POST /documents returned no body.");

        if (deleted)
        {
            using var removed = await client.DeleteAsync(
                new Uri($"/documents/{summary.Id}", UriKind.Relative), cancellationToken);

            removed.EnsureSuccessStatusCode();
        }

        return summary.Id;
    }

    /// <summary>
    /// Provisions a user through the product's own API (§12, register row 25).
    /// </summary>
    /// <remarks>
    /// <c>GET /me</c> is what provisions a user row on first contact, so this
    /// is the path a real person takes. It used to write the row directly,
    /// which meant every test depending on a user row depended on this file
    /// agreeing with <c>UserResolver</c> about what one looks like.
    /// </remarks>
    public async Task<Guid> CreateUserAsync(
        string subject, CancellationToken cancellationToken = default)
    {
        using var client = ClientFor(subject);

        using var response = await client.GetAsync(
            new Uri("/me", UriKind.Relative), cancellationToken);

        response.EnsureSuccessStatusCode();

        var identity = await response.Content.ReadFromJsonAsync<Identity>(cancellationToken)
            ?? throw new InvalidOperationException("GET /me returned no body.");

        return identity.UserId;
    }
}

/// <summary>Counts role lookups without changing any answer.</summary>
public sealed class CountingDocumentRoles : IDocumentRoles
{
    private int _lookups;

    internal IDocumentRoles? Inner { get; set; }

    public int Lookups => Volatile.Read(ref _lookups);

    public Task<Role?> GetRoleAsync(Guid documentId, Guid userId, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookups);
        return Inner!.GetRoleAsync(documentId, userId, cancellationToken);
    }
}
