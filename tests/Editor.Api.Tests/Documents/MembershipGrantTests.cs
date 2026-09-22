using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's grant, and §13.31's problem: two mechanisms satisfy §7's five-second
/// bound, so a test asserting the bound tests neither.
/// </summary>
/// <remarks>
/// <para>
/// Eager invalidation over Redis pub/sub and the cache's own five-second TTL
/// both make a membership change visible inside §7's bound. Wire this endpoint
/// to the undecorated <c>DocumentRoleReader</c> instead of to
/// <see cref="InvalidatingDocumentRoleWriter"/> and every functional test in
/// this file still passes: the row is right in Postgres and the stale entry
/// expires on schedule.
/// </para><para>
/// So <see cref="A_grant_is_visible_faster_than_the_cache_could_have_expired"/>
/// asserts a bound the TTL cannot meet, and checks its own precondition — that
/// less than the TTL has elapsed since the stale entry was created — because a
/// slow machine would otherwise turn the TTL into a passing result and the test
/// would report a mechanism it had not exercised.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class MembershipGrantTests
{
    private static readonly Uri Documents = new("/documents", UriKind.Relative);

    /// <summary>
    /// How quickly the eager path must land, and it is not §7's number.
    /// </summary>
    /// <remarks>
    /// Deliberately far below <see cref="DocumentRoleCacheOptions.MaximumTtl"/>.
    /// Asserting five seconds here would assert the requirement rather than the
    /// mechanism, which is exactly §13.31's failure.
    /// </remarks>
    private static readonly TimeSpan EagerBudget = TimeSpan.FromSeconds(1);

    private readonly EditorFixture _fixture;

    public MembershipGrantTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_grant_is_visible_faster_than_the_cache_could_have_expired()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-fast");
        using var invitee = factory.ClientFor("grant-invitee-fast");

        var document = await CreateAsync(owner, "Shared");
        var inviteeId = await factory.CreateUserAsync(
            "grant-invitee-fast", TestContext.Current.CancellationToken);

        // Populates the negative cache entry: "this user has no role here",
        // held for the full TTL. Without this the grant would be observed
        // against a cold cache and the eager path would never be involved.
        using (var before = await invitee.GetAsync(Document(document.Id), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        }

        var since = Stopwatch.StartNew();

        using (var grant = await owner.PutAsJsonAsync(
            Member(document.Id, inviteeId),
            new GrantRoleRequest(Role.Editor),
            TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
        }

        using var after = await invitee.GetAsync(Document(document.Id), TestContext.Current.CancellationToken);
        var elapsed = since.Elapsed;

        // The precondition this result depends on, checked rather than assumed.
        // If the machine were slow enough for the stale entry to have expired on
        // its own, the assertion below would be about the TTL and would say so
        // by passing for the wrong reason.
        Assert.True(
            elapsed < EagerBudget,
            $"Took {elapsed.TotalMilliseconds:F0} ms, which is not decisively inside the "
            + $"{DocumentRoleCacheOptions.MaximumTtl.TotalSeconds:F0} s TTL. This result cannot "
            + "distinguish the eager invalidation from the cache expiring on its own.");

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task A_granted_editor_can_open_the_document_and_appears_in_the_member_list()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-listed");
        using var invitee = factory.ClientFor("grant-invitee-listed");

        var document = await CreateAsync(owner, "Listed");
        var inviteeId = await factory.CreateUserAsync(
            "grant-invitee-listed", TestContext.Current.CancellationToken);

        var granted = await GrantAsync(owner, document.Id, inviteeId, Role.Editor);
        Assert.Equal(Role.Editor, granted.Role);

        var theirs = await invitee.GetFromJsonAsync<DocumentSummary>(
            Document(document.Id), TestContext.Current.CancellationToken);

        Assert.NotNull(theirs);
        Assert.Equal(Role.Editor, theirs.Role);

        var members = await MembersAsync(owner, document.Id);
        Assert.Contains(members, member => member.UserId == inviteeId && member.Role == Role.Editor);

        // The owner's own row, written at creation, is in the same listing.
        Assert.Contains(members, member => member.Role == Role.Owner);

        // And it reaches the invitee's list of what they can open, which is the
        // whole point of the grant.
        var reachable = await invitee.GetFromJsonAsync<List<DocumentSummary>>(
            Documents, TestContext.Current.CancellationToken);

        Assert.NotNull(reachable);
        Assert.Contains(reachable, entry => entry.Id == document.Id);
    }

    [Fact]
    public async Task A_grant_can_be_changed_and_the_role_is_replaced_rather_than_added()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-change");

        var document = await CreateAsync(owner, "Changing");
        var inviteeId = await factory.CreateUserAsync(
            "grant-invitee-change", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, inviteeId, Role.Editor);
        var demoted = await GrantAsync(owner, document.Id, inviteeId, Role.Viewer);

        Assert.Equal(Role.Viewer, demoted.Role);

        var members = await MembersAsync(owner, document.Id);
        var theirs = Assert.Single(members, member => member.UserId == inviteeId);
        Assert.Equal(Role.Viewer, theirs.Role);
    }

    [Fact]
    public async Task An_editor_cannot_grant_and_is_told_so_rather_than_told_nothing()
    {
        // §7's other half: 403 rather than 404, because an editor can already
        // see the document. Concealing it here would conceal nothing and would
        // leave them unable to tell a permission problem from a missing one.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-editor-cannot");
        using var editor = factory.ClientFor("grant-editor-cannot");

        var document = await CreateAsync(owner, "Not yours to share");
        var editorId = await factory.CreateUserAsync(
            "grant-editor-cannot", TestContext.Current.CancellationToken);
        var thirdId = await factory.CreateUserAsync(
            "grant-third-party", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, editorId, Role.Editor);

        using var attempt = await editor.PutAsJsonAsync(
            Member(document.Id, thirdId),
            new GrantRoleRequest(Role.Editor),
            TestContext.Current.CancellationToken);

        using var listing = await editor.GetAsync(
            Members(document.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listing.StatusCode);
    }

    [Fact]
    public async Task A_stranger_granting_gets_the_same_404_as_for_a_document_that_does_not_exist()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-stranger");
        using var stranger = factory.ClientFor("grant-stranger");

        var document = await CreateAsync(owner, "Private");
        var targetId = await factory.CreateUserAsync(
            "grant-stranger-target", TestContext.Current.CancellationToken);

        using var real = await stranger.PutAsJsonAsync(
            Member(document.Id, targetId),
            new GrantRoleRequest(Role.Editor),
            TestContext.Current.CancellationToken);

        using var imaginary = await stranger.PutAsJsonAsync(
            Member(Guid.CreateVersion7(), targetId),
            new GrantRoleRequest(Role.Editor),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, real.StatusCode);
        Assert.Equal(imaginary.StatusCode, real.StatusCode);
    }

    [Fact]
    public async Task Granting_to_a_user_who_has_never_signed_in_says_so()
    {
        // §9's stated limitation, and §13.13's rule: the caller can act on this.
        // Failing at a foreign key would be a 500 that tells them nothing.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-unknown");

        var document = await CreateAsync(owner, "Unknown invitee");

        using var response = await owner.PutAsJsonAsync(
            Member(document.Id, Guid.CreateVersion7()),
            new GrantRoleRequest(Role.Editor),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("signed in", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_cannot_change_their_own_role()
    {
        // The stored row and the effective role would disagree: GetRoleAsync
        // answers Owner from documents.owner_id whatever document_members says.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-self");

        var document = await CreateAsync(owner, "Mine alone");
        var ownerId = await factory.CreateUserAsync(
            "grant-owner-self", TestContext.Current.CancellationToken);

        using var response = await owner.PutAsJsonAsync(
            Member(document.Id, ownerId),
            new GrantRoleRequest(Role.Viewer),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var members = await MembersAsync(owner, document.Id);
        Assert.Equal(Role.Owner, Assert.Single(members, member => member.UserId == ownerId).Role);
    }

    [Fact]
    public async Task A_role_outside_the_enum_is_refused()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("grant-owner-bad-role");

        var document = await CreateAsync(owner, "Bad role");
        var targetId = await factory.CreateUserAsync(
            "grant-bad-role-target", TestContext.Current.CancellationToken);

        using var response = await owner.PutAsJsonAsync(
            Member(document.Id, targetId),
            new GrantRoleRequest((Role)99),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Uri Document(Guid id) => new($"/documents/{id}", UriKind.Relative);

    private static Uri Members(Guid id) => new($"/documents/{id}/members", UriKind.Relative);

    private static Uri Member(Guid documentId, Guid userId) =>
        new($"/documents/{documentId}/members/{userId}", UriKind.Relative);

    private static async Task<IReadOnlyList<DocumentMemberEntry>> MembersAsync(
        HttpClient client, Guid documentId)
    {
        var members = await client.GetFromJsonAsync<List<DocumentMemberEntry>>(
            Members(documentId), TestContext.Current.CancellationToken);

        Assert.NotNull(members);
        return members;
    }

    private static async Task<DocumentMemberEntry> GrantAsync(
        HttpClient client, Guid documentId, Guid userId, Role role)
    {
        using var response = await client.PutAsJsonAsync(
            Member(documentId, userId), new GrantRoleRequest(role), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var granted = await response.Content.ReadFromJsonAsync<DocumentMemberEntry>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(granted);
        return granted;
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
