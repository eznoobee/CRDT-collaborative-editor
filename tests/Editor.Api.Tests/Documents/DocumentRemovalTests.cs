using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's <c>DELETE /documents/{id}</c> (register row 23).
/// </summary>
/// <remarks>
/// <para>
/// <b>The easy half was already true before this task started.</b>
/// <c>documents.deleted_at</c> has existed since Phase 2 and every path that
/// reads the document row filters on it, so a handler that did nothing but set
/// the column would make the listing, the metadata read and a fresh
/// <c>negotiate</c> all behave — and a test that deleted a document and then
/// checked a listing would be testing Phase 2.
/// </para><para>
/// <b>So the question this file is built around is §12's:</b> for a mechanism
/// keyed on an action, who is the legitimate user that never performs it? The
/// enforcement is keyed on <i>reading the document row</i>, and the person who
/// never reads it is <b>the one already connected</b>. Their submissions are
/// checked against the role cache, and so is the sweep that would close them,
/// so nothing in the live path touches the column deletion writes. Predicted
/// before the code, and every assertion here that matters is about that
/// person.
/// </para><para>
/// <b>§13.31 governs the timing.</b> The budget is inside the role cache's TTL,
/// so the TTL expiring on its own cannot produce a pass.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class DocumentRemovalTests
{
    private static readonly Uri Documents = new("/documents", UriKind.Relative);

    /// <summary>A budget the cache expiring on its own cannot meet.</summary>
    private static readonly TimeSpan EagerBudget = TimeSpan.FromSeconds(2.5);

    private readonly EditorFixture _fixture;

    public DocumentRemovalTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Removing_a_document_closes_the_connection_someone_is_holding()
    {
        // THE TEST THIS TASK EXISTS FOR. Nothing here submits anything: a
        // reader with the document open is exactly the principal the deleted_at
        // filter cannot reach, and without the cache invalidation this
        // connection stays open and keeps receiving a document that no longer
        // exists for anybody else.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-live");

        var document = await CreateAsync(owner, "Doomed");
        var memberId = await factory.CreateUserAsync(
            "remove-member-live", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, memberId, Role.Viewer);

        await using var reader = await DocumentClient.JoinAsync(
            factory, "remove-member-live", document.Id);

        var closed = new TaskCompletionSource();
        reader.Connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        var since = Stopwatch.StartNew();
        await RemoveAsync(owner, document.Id);

        var finished = await Task.WhenAny(
            closed.Task, Task.Delay(EagerBudget, TestContext.Current.CancellationToken));
        var elapsed = since.Elapsed;

        Assert.True(
            ReferenceEquals(finished, closed.Task),
            $"The connection was still open {elapsed.TotalMilliseconds:F0} ms after the document "
            + "was removed. Writing deleted_at is not enough: a live connection is checked "
            + "against the role cache, which still holds a role for a document that is gone.");

        Assert.True(
            elapsed < EagerBudget,
            $"Closed after {elapsed.TotalMilliseconds:F0} ms, which is outside the budget the "
            + "cache TTL alone could not have met.");
    }

    [Fact]
    public async Task The_owner_of_a_removed_document_cannot_go_on_writing_to_it()
    {
        // The other live principal. An owner has the document open, removes it
        // from another tab, and their existing socket must stop accepting work
        // — otherwise the document keeps growing after it is gone, and the
        // operations land in a log nobody can ever read back.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-writing");

        var document = await CreateAsync(owner, "Still typing");

        await using var writer = await DocumentClient.JoinAsync(
            factory, "remove-owner-writing", document.Id);

        Assert.Null((await writer.SubmitAsync(writer.Writer.Type("before"))).Code);

        await RemoveAsync(owner, document.Id);

        // Either the submission is refused or the connection is gone. Both are
        // correct; accepting the batch is not.
        var deadline = DateTime.UtcNow + EagerBudget;
        string? code = null;
        var disconnected = false;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                code = (await writer.SubmitAsync(writer.Writer.Type("x"))).Code;
                if (code is not null)
                {
                    break;
                }
            }
            catch (Exception)
            {
                disconnected = true;
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(
            disconnected || code is not null,
            "the server went on accepting operations into a document that had been removed");
    }

    [Fact]
    public async Task An_editor_cannot_remove_the_document()
    {
        // Being able to write in a document is not being able to end it for
        // everyone else. Forbidden rather than not-found: the caller has a role,
        // so §7's 404-not-403 rule does not apply — hiding the document from
        // someone who can already read it would tell them nothing true.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-role");
        using var editor = factory.ClientFor("remove-editor-role");

        var document = await CreateAsync(owner, "Not yours");
        var editorId = await factory.CreateUserAsync(
            "remove-editor-role", TestContext.Current.CancellationToken);

        await GrantAsync(owner, document.Id, editorId, Role.Editor);

        using var response = await editor.DeleteAsync(
            One(document.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // And it really is still there, which is what separates "refused" from
        // "refused after doing it".
        using var still = await owner.GetAsync(One(document.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    [Fact]
    public async Task A_stranger_is_told_it_is_not_there_rather_than_that_they_may_not()
    {
        // §7's rule. A 403 to a non-member confirms the document exists.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-stranger");
        using var stranger = factory.ClientFor("remove-stranger");

        var document = await CreateAsync(owner, "Private");

        using var response = await stranger.DeleteAsync(
            One(document.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Removing_it_twice_is_not_found_the_second_time()
    {
        // Idempotency falls out of the filter rather than a case of its own: a
        // document that is not there is the same 404 a stranger gets.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-twice");

        var document = await CreateAsync(owner, "Twice");

        await RemoveAsync(owner, document.Id);

        using var again = await owner.DeleteAsync(
            One(document.Id), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_removed_document_leaves_the_listing_and_cannot_be_opened_again()
    {
        // The half that was already true, asserted anyway because it is the
        // observable behaviour a person asks for — and because a fix aimed at
        // the live connection could break it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var owner = factory.ClientFor("remove-owner-listing");

        var document = await CreateAsync(owner, "Gone");

        await RemoveAsync(owner, document.Id);

        using var listed = await owner.GetAsync(Documents, TestContext.Current.CancellationToken);
        var summaries = await listed.Content.ReadFromJsonAsync<IReadOnlyList<DocumentSummary>>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(summaries);
        Assert.DoesNotContain(summaries, summary => summary.Id == document.Id);

        using var fetched = await owner.GetAsync(
            One(document.Id), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, fetched.StatusCode);

        using var negotiated = await owner.PostAsJsonAsync(
            new Uri($"/documents/{document.Id}/negotiate", UriKind.Relative),
            new { replicaId = (Guid?)null },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, negotiated.StatusCode);
    }

    private static Uri One(Guid documentId) =>
        new($"/documents/{documentId}", UriKind.Relative);

    private static Uri Member(Guid documentId, Guid userId) =>
        new($"/documents/{documentId}/members/{userId}", UriKind.Relative);

    private static async Task RemoveAsync(HttpClient client, Guid documentId)
    {
        using var response = await client.DeleteAsync(
            One(documentId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task GrantAsync(
        HttpClient client, Guid documentId, Guid userId, Role role)
    {
        using var response = await client.PutAsJsonAsync(
            Member(documentId, userId),
            new GrantRoleRequest(role),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
