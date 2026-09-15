using System.Net;
using System.Net.Http.Json;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §7's membership decision: where 404-not-403 is decided and where the server
/// chooses the replica id.
/// </summary>
[Collection(nameof(EditorTests))]
public sealed class NegotiateTests
{
    private readonly EditorFixture _fixture;

    public NegotiateTests(EditorFixture fixture) => _fixture = fixture;

    private static Uri Negotiate(Guid documentId) =>
        new($"/documents/{documentId}/negotiate", UriKind.Relative);

    [Fact]
    public async Task An_anonymous_call_is_refused_by_the_real_scheme()
    {
        // The other tests here replace token validation so they can be about
        // authorization. This one does not: without it, a negotiate endpoint
        // that had lost RequireAuthorization would pass every test in the file.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, testAuthentication: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            Negotiate(Guid.CreateVersion7()), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("not-a-member")]
    public async Task A_document_the_caller_cannot_see_is_indistinguishable_from_one_that_does_not_exist(
        string scenario)
    {
        // §7: authorization failures return 404 for documents the caller cannot
        // see. All three of these must produce the same answer, because any
        // difference between them is a way to enumerate documents.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var owner = await factory.CreateUserAsync("owner-" + scenario, TestContext.Current.CancellationToken);
        var documentId = scenario switch
        {
            "missing" => Guid.CreateVersion7(),
            "deleted" => await factory.CreateDocumentAsync(
                owner, deleted: true, TestContext.Current.CancellationToken),
            _ => await factory.CreateDocumentAsync(owner, cancellationToken: TestContext.Current.CancellationToken),
        };

        using var client = factory.ClientFor("stranger-" + scenario);
        using var response = await client.PostAsync(
            Negotiate(documentId), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_deleted_document_is_closed_to_its_own_owner()
    {
        // The owner column grants a role on its own, which is what keeps an
        // owner from being locked out of their document. Soft deletion has to
        // win over that, or "deleted" means nothing.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var owner = await factory.CreateUserAsync("owner-deleted", TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, deleted: true, TestContext.Current.CancellationToken);

        using var client = factory.ClientFor("owner-deleted");
        using var response = await client.PostAsync(
            Negotiate(documentId), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_member_gets_a_ticket_bound_to_a_replica_the_server_chose()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var owner = await factory.CreateUserAsync("owner-member", TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        using var client = factory.ClientFor("owner-member");
        var negotiated = await Post(client, documentId);

        Assert.Equal(documentId, negotiated.DocumentId);
        Assert.Equal(Role.Owner, negotiated.Role);
        Assert.NotEqual(Guid.Empty, negotiated.ReplicaId);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var replica = await context.DocumentReplicas.SingleAsync(
            row => row.DocumentId == documentId && row.ReplicaId == negotiated.ReplicaId,
            TestContext.Current.CancellationToken);

        // §13.12: the binding is only a check if the server chose it, and it is
        // only enforceable later if it was written down against the user.
        Assert.Equal(owner, replica.UserId);
        Assert.Equal(0, replica.OperationCount);
        Assert.Null(replica.RetiredAt);

        // The ticket carries that same binding, and redeems once.
        var tickets = factory.Services.GetRequiredService<IConnectTicketStore>();
        var binding = await tickets.RedeemAsync(negotiated.Ticket, TestContext.Current.CancellationToken);

        Assert.Equal(
            new ConnectionBinding(owner, documentId, negotiated.ReplicaId, Role.Owner), binding);
    }

    [Fact]
    public async Task Every_negotiate_gets_its_own_replica_id()
    {
        // Two tabs are two replicas. Reusing one replica id across connections
        // would make their operations share a sequence space, and §5's density
        // requirement would be violated by the second tab's first keystroke.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var owner = await factory.CreateUserAsync("owner-two-tabs", TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        using var client = factory.ClientFor("owner-two-tabs");
        var first = await Post(client, documentId);
        var second = await Post(client, documentId);

        Assert.NotEqual(first.ReplicaId, second.ReplicaId);
        Assert.NotEqual(first.Ticket, second.Ticket);
    }

    [Fact]
    public async Task A_viewer_may_connect()
    {
        // §7: viewers receive broadcasts. Refusing them at negotiate would make
        // read-only sharing impossible; the write rejection happens at the hub.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var owner = await factory.CreateUserAsync("owner-viewer", TestContext.Current.CancellationToken);
        var viewer = await factory.CreateUserAsync("viewer", TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
                .SetRoleAsync(documentId, viewer, Role.Viewer, owner, TestContext.Current.CancellationToken);
        }

        using var client = factory.ClientFor("viewer");
        var negotiated = await Post(client, documentId);

        Assert.Equal(Role.Viewer, negotiated.Role);
    }

    private static async Task<Negotiated> Post(HttpClient client, Guid documentId)
    {
        using var response = await client.PostAsync(
            Negotiate(documentId), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var negotiated = await response.Content.ReadFromJsonAsync<Negotiated>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(negotiated);
        return negotiated;
    }
}
