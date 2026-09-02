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

    public EditorApiFactory(EditorFixture fixture, bool testAuthentication = true)
    {
        _fixture = fixture;
        _testAuthentication = testAuthentication;
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

    /// <summary>Creates a document owned by <paramref name="ownerId"/>.</summary>
    public async Task<Guid> CreateDocumentAsync(
        Guid ownerId, bool deleted = false, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var id = Guid.CreateVersion7();
        context.Documents.Add(new Document
        {
            Id = id,
            OwnerId = ownerId,
            Title = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        });

        await context.SaveChangesAsync(cancellationToken);
        return id;
    }

    /// <summary>Provisions a user row directly, the way a first negotiate would.</summary>
    public async Task<Guid> CreateUserAsync(string subject, CancellationToken cancellationToken = default)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var existing = await context.Users
            .Where(user => user.OidcIssuer == ApiFactory.Issuer && user.OidcSubject == subject)
            .Select(user => (Guid?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Value;
        }

        var id = Guid.CreateVersion7();
        context.Users.Add(new User
        {
            Id = id,
            OidcIssuer = ApiFactory.Issuer,
            OidcSubject = subject,
            DisplayName = subject,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await context.SaveChangesAsync(cancellationToken);
        return id;
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
