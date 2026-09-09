using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's revoke, and §7's rule that a membership change reaches a live
/// connection rather than only the next one.
/// </summary>
/// <remarks>
/// <para>
/// The easy half — a revoked member's next request is refused — was already
/// true in Phase 3 and proves nothing about a session that is already open. The
/// half that matters is the connection someone is holding: §7's per-submission
/// role check covers a revoked <em>writer</em> and does nothing about a revoked
/// <em>reader</em>, who sends nothing and would go on receiving every broadcast
/// on the document for as long as the socket stayed open.
/// </para><para>
/// §13.31 governs the timing assertions here as in the grant tests. Every
/// budget below is decisively inside the role cache's TTL, so the TTL expiring
/// on its own cannot produce a pass, and each test checks that precondition
/// rather than assuming the machine was fast enough.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class MembershipRevocationTests
{
    private static readonly Uri Documents = new("/documents", UriKind.Relative);

    /// <summary>
    /// A budget the cache expiring on its own cannot meet.
    /// </summary>
    /// <remarks>
    /// The default TTL is four seconds and the sweep runs every second, so a
    /// close inside two and a half seconds had to come from the eager
    /// invalidation. Asserting §7's five seconds instead would assert the
    /// requirement and exercise neither mechanism.
    /// </remarks>
    private static readonly TimeSpan EagerBudget = TimeSpan.FromSeconds(2.5);

    private readonly EditorFixture _fixture;

    public MembershipRevocationTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Revoking_removes_the_document_from_what_the_member_can_reach()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("revoke-owner-reach");
        using var member = factory.ClientFor("revoke-member-reach");

        var document = await CreateAsync(owner, "Shared then not");
        var memberId = await factory.CreateUserAsync(
            "revoke-member-reach", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, memberId, Role.Editor);

        var before = await ReachableAsync(member);
        Assert.Contains(before, entry => entry.Id == document.Id);

        // Populates the *positive* cache entry, and this line is load-bearing.
        // Without it the role cache is cold when the revocation lands, the
        // first read afterwards goes to Postgres, and the 404 below arrives
        // without the eager invalidation having done anything — which the
        // sabotage in this task caught: the undecorated writer left this test
        // green. The listing above cannot serve the same purpose, because §9
        // makes it deliberately uncached.
        using (var seen = await member.GetAsync(
            Document(document.Id), TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        }

        var since = Stopwatch.StartNew();
        await RevokeAsync(owner, document.Id, memberId);

        using var refused = await member.GetAsync(
            Document(document.Id), TestContext.Current.CancellationToken);
        var elapsed = since.Elapsed;

        var after = await ReachableAsync(member);

        Assert.True(
            elapsed < EagerBudget,
            $"Took {elapsed.TotalMilliseconds:F0} ms, which is not decisively inside the role "
            + "cache TTL. This result cannot distinguish the eager invalidation from the cache "
            + "expiring on its own.");

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.DoesNotContain(after, entry => entry.Id == document.Id);
    }

    [Fact]
    public async Task Revoking_a_membership_that_is_already_gone_succeeds()
    {
        // Idempotent on purpose: a retry after a dropped response is the same
        // request, and a 404 here would make it look like a different failure.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("revoke-owner-twice");

        var document = await CreateAsync(owner, "Twice");
        var memberId = await factory.CreateUserAsync(
            "revoke-member-twice", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, memberId, Role.Editor);
        await RevokeAsync(owner, document.Id, memberId);
        await RevokeAsync(owner, document.Id, memberId);

        var members = await MembersAsync(owner, document.Id);
        Assert.DoesNotContain(members, member => member.UserId == memberId);
    }

    [Fact]
    public async Task The_owner_cannot_be_removed_from_their_own_document()
    {
        // A document whose owner has no membership row is one nobody can grant
        // on again, and the recovery for that is someone editing the database.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("revoke-owner-self");

        var document = await CreateAsync(owner, "Mine");
        var ownerId = await factory.CreateUserAsync(
            "revoke-owner-self", TestContext.Current.CancellationToken);

        using var response = await owner.DeleteAsync(
            Member(document.Id, ownerId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var members = await MembersAsync(owner, document.Id);
        Assert.Contains(members, member => member.UserId == ownerId && member.Role == Role.Owner);
    }

    [Fact]
    public async Task An_editor_cannot_revoke_and_a_stranger_cannot_see_that_there_is_anything_to_revoke()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("revoke-owner-permissions");
        using var editor = factory.ClientFor("revoke-editor-permissions");
        using var stranger = factory.ClientFor("revoke-stranger-permissions");

        var document = await CreateAsync(owner, "Permissions");
        var editorId = await factory.CreateUserAsync(
            "revoke-editor-permissions", TestContext.Current.CancellationToken);
        var victimId = await factory.CreateUserAsync(
            "revoke-victim-permissions", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, editorId, Role.Editor);
        await GrantAsync(owner, document.Id, victimId, Role.Viewer);

        using var byEditor = await editor.DeleteAsync(
            Member(document.Id, victimId), TestContext.Current.CancellationToken);
        using var byStranger = await stranger.DeleteAsync(
            Member(document.Id, victimId), TestContext.Current.CancellationToken);

        // 403 for the editor, who can already see the document; 404 for the
        // stranger, who must not learn that it exists.
        Assert.Equal(HttpStatusCode.Forbidden, byEditor.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);

        // And neither attempt did anything.
        var members = await MembersAsync(owner, document.Id);
        Assert.Contains(members, member => member.UserId == victimId);
    }

    [Fact]
    public async Task A_revoked_reader_loses_the_connection_it_is_already_holding()
    {
        // The test this task exists for. Nothing here submits anything: a
        // revoked reader is exactly the case the per-operation role check
        // cannot reach, and before the membership sweep this connection stayed
        // open and kept receiving the document's text.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("revoke-owner-live");

        var document = await CreateAsync(owner, "Live");
        var memberId = await factory.CreateUserAsync(
            "revoke-member-live", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, memberId, Role.Viewer);

        await using var reader = await DocumentClient.JoinAsync(
            factory, "revoke-member-live", document.Id);

        var closed = new TaskCompletionSource();
        reader.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var sweep = factory.Services.GetRequiredService<MembershipSweep>();
        var before = sweep.RevokedConnections;

        var since = Stopwatch.StartNew();
        await RevokeAsync(owner, document.Id, memberId);

        var finished = await Task.WhenAny(
            closed.Task, Task.Delay(EagerBudget, TestContext.Current.CancellationToken));
        var elapsed = since.Elapsed;

        Assert.True(
            ReferenceEquals(finished, closed.Task),
            $"The connection was still open after {elapsed.TotalMilliseconds:F0} ms. §7 requires a "
            + "revoked member to stop receiving within five seconds, and this budget is inside the "
            + "role cache TTL so that the TTL alone cannot satisfy it.");

        // The mechanism, asserted directly rather than inferred from a socket
        // that closed (§13.15): a connection dropped for any other reason would
        // satisfy the assertion above and leave this counter alone.
        Assert.True(sweep.RevokedConnections > before);
    }

    [Fact]
    public async Task A_member_demoted_to_viewer_keeps_the_connection_and_loses_the_write()
    {
        // The other side of the same mechanism, and the one that says the sweep
        // closes connections for the stated reason rather than for any change.
        // §9's table: forbidden means read-only, still connected, outbox kept.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("demote-owner");

        var document = await CreateAsync(owner, "Demotion");
        var memberId = await factory.CreateUserAsync(
            "demote-member", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, memberId, Role.Editor);

        await using var member = await DocumentClient.JoinAsync(factory, "demote-member", document.Id);

        var accepted = await member.SubmitAsync();
        Assert.Null(accepted.Code);

        await GrantAsync(owner, document.Id, memberId, Role.Viewer);

        var refused = await WaitForRefusalAsync(member);

        Assert.Equal(HubErrors.Forbidden, refused.Code);
        Assert.Equal(HubConnectionState.Connected, member.Connection.State);
    }

    /// <summary>
    /// Submits until the demotion lands, or fails inside the eager budget.
    /// </summary>
    /// <remarks>
    /// A single submission immediately after the grant would be a race with the
    /// invalidation rather than a test of it, and the flaky direction is the
    /// wrong one — it would pass by accident when the change had not landed.
    /// </remarks>
    private static async Task<SubmitResult> WaitForRefusalAsync(DocumentClient client)
    {
        var since = Stopwatch.StartNew();
        SubmitResult last;

        do
        {
            last = await client.SubmitAsync();
            if (last.Code is not null)
            {
                return last;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        while (since.Elapsed < EagerBudget);

        Assert.Fail(
            $"The demotion had not taken effect after {since.Elapsed.TotalMilliseconds:F0} ms, "
            + "which is inside the role cache TTL: the eager invalidation did not arrive.");
        return last;
    }

    private static Uri Document(Guid id) => new($"/documents/{id}", UriKind.Relative);

    private static Uri Members(Guid id) => new($"/documents/{id}/members", UriKind.Relative);

    private static Uri Member(Guid documentId, Guid userId) =>
        new($"/documents/{documentId}/members/{userId}", UriKind.Relative);

    private static async Task<IReadOnlyList<DocumentSummary>> ReachableAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<List<DocumentSummary>>(
            Documents, TestContext.Current.CancellationToken);

        Assert.NotNull(list);
        return list;
    }

    private static async Task<IReadOnlyList<DocumentMemberEntry>> MembersAsync(
        HttpClient client, Guid documentId)
    {
        var members = await client.GetFromJsonAsync<List<DocumentMemberEntry>>(
            Members(documentId), TestContext.Current.CancellationToken);

        Assert.NotNull(members);
        return members;
    }

    private static async Task GrantAsync(HttpClient client, Guid documentId, Guid userId, Role role)
    {
        using var response = await client.PutAsJsonAsync(
            Member(documentId, userId), new GrantRoleRequest(role), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task RevokeAsync(HttpClient client, Guid documentId, Guid userId)
    {
        using var response = await client.DeleteAsync(
            Member(documentId, userId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
