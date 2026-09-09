using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Domain;
using Editor.Api.Tests.Hubs;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's <c>POST /documents</c>: the path that did not exist, and register rows
/// 15 and 16 with it.
/// </summary>
/// <remarks>
/// <para>
/// The named vacuity risk, and it is specific to this endpoint.
/// <c>DocumentRoleReader</c> treats the <c>owner_id</c> column as authoritative
/// on its own, deliberately — a document whose owner row was never mirrored into
/// <c>document_members</c> would otherwise lock its owner out, and the recovery
/// for that is someone editing the database. That kindness means the obvious
/// test here, "create a document and then edit it", passes whether or not the
/// membership row was ever written.
/// </para><para>
/// So the row is asserted directly, in the database, rather than inferred from
/// the owner being able to use what they made. The sabotage that keeps this
/// honest is deleting the <c>SetRoleAsync</c> call in <c>CreateAsync</c>: the
/// behavioural tests here stay green and
/// <see cref="Creating_a_document_writes_the_membership_row_and_not_only_the_owner_column"/>
/// goes red.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class DocumentCreationTests
{
    private static readonly Uri Documents = new("/documents", UriKind.Relative);

    private readonly EditorFixture _fixture;

    public DocumentCreationTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_anonymous_call_is_refused_by_the_real_scheme()
    {
        // Without this the whole file would pass against an endpoint that had
        // lost RequireAuthorization, because every other test here supplies an
        // identity through the test scheme.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, testAuthentication: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            Documents, new CreateDocumentRequest("anything"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_document_writes_the_membership_row_and_not_only_the_owner_column()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("creator-membership-row");

        var created = await CreateAsync(client, "Notes");

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var ownerId = await context.Documents
            .Where(document => document.Id == created.Id)
            .Select(document => document.OwnerId)
            .SingleAsync(TestContext.Current.CancellationToken);

        var member = await context.DocumentMembers
            .SingleOrDefaultAsync(
                row => row.DocumentId == created.Id && row.UserId == ownerId,
                TestContext.Current.CancellationToken);

        Assert.NotNull(member);
        Assert.Equal(Role.Owner, member.Role);

        // Granted by themselves, which is the only honest answer for a creation
        // and the value the members listing will show.
        Assert.Equal(ownerId, member.GrantedBy);
    }

    [Fact]
    public async Task The_creator_owns_what_they_created()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("creator-owns");

        var created = await CreateAsync(client, "  Trimmed  ");

        Assert.Equal(Role.Owner, created.Role);
        Assert.Equal("Trimmed", created.Title);
        Assert.NotEqual(Guid.Empty, created.Id);

        var fetched = await client.GetFromJsonAsync<DocumentSummary>(
            new Uri($"/documents/{created.Id}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("Trimmed", fetched.Title);
        Assert.Equal(Role.Owner, fetched.Role);
    }

    [Fact]
    public async Task A_document_created_by_someone_else_is_a_404()
    {
        // §7: not 403. A different status for "exists but not yours" than for
        // "does not exist" is an enumeration oracle, and the id here is real.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("creator-private");
        using var stranger = factory.ClientFor("stranger-private");

        var created = await CreateAsync(owner, "Private");

        using var theirs = await stranger.GetAsync(
            new Uri($"/documents/{created.Id}", UriKind.Relative), TestContext.Current.CancellationToken);
        using var absent = await stranger.GetAsync(
            new Uri($"/documents/{Guid.CreateVersion7()}", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Equal(absent.StatusCode, theirs.StatusCode);
    }

    [Fact]
    public async Task A_deleted_document_is_a_404_to_its_own_owner()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("creator-deleted");

        var created = await CreateAsync(client, "Doomed");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.Documents
                .Where(document => document.Id == created.Id)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(document => document.DeletedAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        using var response = await client.GetAsync(
            new Uri($"/documents/{created.Id}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task A_document_without_a_title_is_refused(string? title)
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("creator-untitled");

        using var response = await client.PostAsJsonAsync(
            Documents, new CreateDocumentRequest(title), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_title_longer_than_the_column_is_refused_rather_than_truncated()
    {
        // The column is 512 characters. Truncating would store something the
        // caller did not ask for; failing at the database would be a 500.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("creator-long-title");

        var title = new string('x', DocumentsEndpoints.MaximumTitleLength + 1);

        using var response = await client.PostAsJsonAsync(
            Documents, new CreateDocumentRequest(title), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<DocumentSummary> CreateAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync(
            Documents, new CreateDocumentRequest(title), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<DocumentSummary>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(created);
        return created;
    }
}
