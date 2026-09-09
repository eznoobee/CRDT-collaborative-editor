using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's <c>GET /documents</c> — the answer to "what can I open", which nothing
/// in this product could give before Phase 6 (register row 16).
/// </summary>
/// <remarks>
/// The named vacuity risk: a single-user test returns the same list whether the
/// query filters by membership or selects every row in the table, so every test
/// here holds two users with documents of their own. The assertion that matters
/// is an *absence* — the other person's document is not in my list — and an
/// absence assertion is satisfied by an endpoint that returns nothing at all,
/// which is why both lists are asserted non-empty in the same test.
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class DocumentListingTests
{
    private static readonly Uri Documents = new("/documents", UriKind.Relative);

    private readonly EditorFixture _fixture;

    public DocumentListingTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_anonymous_call_is_refused_by_the_real_scheme()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, testAuthentication: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Documents, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Two_users_each_see_their_own_documents_and_not_the_others()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var first = factory.ClientFor("lister-first");
        using var second = factory.ClientFor("lister-second");

        var mine = await CreateAsync(first, "Mine");
        var theirs = await CreateAsync(second, "Theirs");

        var myList = await ListAsync(first);
        var theirList = await ListAsync(second);

        // Both non-empty, so "returns nothing" cannot satisfy the absences below.
        Assert.NotEmpty(myList);
        Assert.NotEmpty(theirList);

        Assert.Contains(myList, entry => entry.Id == mine.Id);
        Assert.DoesNotContain(myList, entry => entry.Id == theirs.Id);

        Assert.Contains(theirList, entry => entry.Id == theirs.Id);
        Assert.DoesNotContain(theirList, entry => entry.Id == mine.Id);
    }

    [Fact]
    public async Task A_new_user_sees_an_empty_list_rather_than_an_error()
    {
        // The state every user is in on their first load, and the state the
        // client has to render as "create one" rather than as a failure.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var stranger = factory.ClientFor("lister-brand-new");

        // Somebody else's document exists, so an empty answer here is a filter
        // working rather than an empty table.
        using var other = factory.ClientFor("lister-incumbent");
        await CreateAsync(other, "Existing");

        var list = await ListAsync(stranger);

        Assert.Empty(list);
    }

    [Fact]
    public async Task The_owner_appears_in_their_own_list_through_the_membership_row()
    {
        // 6.1's row, observed behaviourally. This is the assertion that would
        // have caught a missing document_members insert without reading the
        // database — and it only works because the listing is not built from
        // the owner column alone.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("lister-owner-row");

        var created = await CreateAsync(client, "Owned");
        var list = await ListAsync(client);

        var entry = Assert.Single(list, row => row.Id == created.Id);
        Assert.Equal(Role.Owner, entry.Role);
        Assert.Equal("Owned", entry.Title);
    }

    [Fact]
    public async Task A_soft_deleted_document_leaves_the_list()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("lister-deleted");

        var kept = await CreateAsync(client, "Kept");
        var doomed = await CreateAsync(client, "Doomed");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.Documents
                .Where(document => document.Id == doomed.Id)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(document => document.DeletedAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        var list = await ListAsync(client);

        // The kept one is still there, so this is a filter rather than a list
        // that broke.
        Assert.Contains(list, entry => entry.Id == kept.Id);
        Assert.DoesNotContain(list, entry => entry.Id == doomed.Id);
    }

    [Fact]
    public async Task The_list_is_most_recently_updated_first()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.ClientFor("lister-order");

        var older = await CreateAsync(client, "Older");
        var newer = await CreateAsync(client, "Newer");

        // Creation timestamps come from one TimeProvider read per request and
        // can land in the same tick, so the order is made unambiguous rather
        // than assumed — an ordering test that passes on a tie proves nothing.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.Documents
                .Where(document => document.Id == newer.Id)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(
                        document => document.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(5)),
                    TestContext.Current.CancellationToken);
        }

        var list = await ListAsync(client);
        var ids = list.Select(entry => entry.Id).ToList();

        Assert.True(ids.IndexOf(newer.Id) < ids.IndexOf(older.Id));
    }

    private static async Task<IReadOnlyList<DocumentSummary>> ListAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<DocumentSummary>>(
            Documents, TestContext.Current.CancellationToken);

        Assert.NotNull(list);
        return list;
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
