using System.Diagnostics;
using System.Net.Http.Json;
using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §7 at the hub: ticket authentication, the two authorization checks, and the
/// error codes that must not say more than they should.
/// </summary>
[Collection(nameof(EditorTests))]
public sealed class EditorHubTests
{
    private readonly EditorFixture _fixture;

    public EditorHubTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_connection_without_a_ticket_is_refused()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        await using var connection = Connect(factory, ticket: null);

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_ticket_that_was_never_issued_is_refused()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        // Ticket-shaped, so it gets past the shape check and is actually looked
        // up. A malformed one would prove less.
        await using var connection = Connect(factory, new string('A', 43));

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_ticket_cannot_be_used_by_a_second_connection()
    {
        // §7's single-use rule, at the level that matters: not "GETDEL removes
        // the key" but "the second browser tab does not get in".
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (documentId, _, negotiated) = await JoinAsync(factory, "reuse", Role.Editor);

        await using var first = Connect(factory, negotiated.Ticket);
        await first.StartAsync(TestContext.Current.CancellationToken);

        // A successful call, not just a successful start. SignalR completes its
        // handshake before running OnConnectedAsync, so StartAsync returning
        // says nothing about whether the ticket has been redeemed yet — and a
        // test that raced the two would fail on a busy machine roughly as often
        // as it caught anything. A method that needs the binding only succeeds
        // once redemption has happened.
        var accepted = await SubmitAsync(
            first, documentId, negotiated.ReplicaId, new ReplicaWriter(negotiated.ReplicaId).Type("a"));
        Assert.Null(accepted.Code);

        await using var second = Connect(factory, negotiated.Ticket);
        await Assert.ThrowsAnyAsync<Exception>(
            () => second.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Submitting_into_a_document_the_connection_did_not_join_is_not_found()
    {
        // §7's first check. not_found rather than forbidden: the caller may
        // have guessed a real document id and must not learn that they did.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "wrong-document", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var result = await SubmitAsync(connection, Guid.CreateVersion7(), negotiated.ReplicaId);

        AssertRejected(HubErrors.NotFound, result);
    }

    [Fact]
    public async Task Submitting_as_another_replica_is_refused()
    {
        // §7: an operation whose replica id does not match the connection's is
        // rejected. Without it a client could author operations attributed to
        // another replica, and every other replica would converge on the
        // forgery.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "wrong-replica", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var result = await SubmitAsync(connection, negotiated.DocumentId, Guid.CreateVersion7());

        // forbidden, not not_found: the caller can see this document, so there
        // is nothing left to conceal about it.
        AssertRejected(HubErrors.Forbidden, result);
    }

    [Fact]
    public async Task An_editor_may_submit()
    {
        // The negative tests above would all pass against a hub that refused
        // everything.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "editor", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var writer = new ReplicaWriter(negotiated.ReplicaId);
        var result = await SubmitAsync(
            connection, negotiated.DocumentId, negotiated.ReplicaId, writer.Type("hello"));

        Assert.Null(result.Code);
        Assert.Equal(5, result.Accepted);
    }

    [Fact]
    public async Task A_viewer_write_is_forbidden()
    {
        // §7: viewers receive broadcasts and any write from one is rejected.
        // 403's equivalent, because they can already see the document.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "viewer-write", Role.Viewer);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var result = await SubmitAsync(connection, negotiated.DocumentId, negotiated.ReplicaId);

        AssertRejected(HubErrors.Forbidden, result);
    }

    [Fact]
    public async Task Revocation_takes_effect_on_the_next_operation()
    {
        // The role is read per operation rather than trusted from connect time.
        // A session that kept the role it was issued with would let a removed
        // collaborator keep typing until they closed the tab.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (documentId, userId, negotiated) = await JoinAsync(factory, "revoked", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var writer = new ReplicaWriter(negotiated.ReplicaId);
        Assert.Null((await SubmitAsync(
            connection, documentId, negotiated.ReplicaId, writer.Type("a"))).Code);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
                .RemoveAsync(documentId, userId, TestContext.Current.CancellationToken);
        }

        var result = await SubmitAsync(
            connection, documentId, negotiated.ReplicaId, writer.Type("b"));

        AssertRejected(HubErrors.NotFound, result);
    }

    [Fact]
    public async Task Revocation_lands_within_five_seconds_even_with_no_invalidation_at_all()
    {
        // §7 bounds revocation at five seconds; eager invalidation is what
        // makes it usually immediate. This is the case where the invalidation
        // never happens — a lost pub/sub message, an instance that was
        // disconnected from Redis, a row changed by an operator with psql —
        // and the TTL is the only thing left. The bound has to hold anyway,
        // which is why the test removes the row behind the writer's back
        // instead of calling it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (documentId, userId, negotiated) = await JoinAsync(factory, "ttl-revoked", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var writer = new ReplicaWriter(negotiated.ReplicaId);
        Assert.Null((await SubmitAsync(
            connection, documentId, negotiated.ReplicaId, writer.Type("a"))).Code);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.DocumentMembers
                .Where(member => member.DocumentId == documentId && member.UserId == userId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        var elapsed = Stopwatch.StartNew();
        var bound = DocumentRoleCacheOptions.MaximumTtl + TimeSpan.FromSeconds(2);

        while (elapsed.Elapsed < bound)
        {
            var result = await SubmitAsync(
                connection, documentId, negotiated.ReplicaId, writer.Type("x"));
            if (result.Code is not null)
            {
                AssertRejected(HubErrors.NotFound, result);
                Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, DocumentRoleCacheOptions.MaximumTtl);
                return;
            }
        }

        Assert.Fail(
            $"§7 bounds revocation at {DocumentRoleCacheOptions.MaximumTtl}; "
            + $"the caller could still write after {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task The_cheap_check_runs_first_and_stops_the_expensive_one()
    {
        // §7 orders these deliberately: the document-id comparison is a field
        // comparison and costs nothing, and it exists so that a client
        // submitting into a document it never joined does not get a role lookup
        // per message. Both orders return not_found, so only the count can tell
        // them apart — and getting it backwards turns a rejected message into a
        // cache read per keystroke, which is a denial-of-service with the
        // server's own authorization as the amplifier.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "cheap-first", Role.Editor);

        await using var connection = Connect(factory, negotiated.Ticket);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var before = factory.Roles.Lookups;
        var writer = new ReplicaWriter(negotiated.ReplicaId);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await SubmitAsync(
                connection, Guid.CreateVersion7(), negotiated.ReplicaId, writer.Type("a"));
            AssertRejected(HubErrors.NotFound, result);
        }

        Assert.Equal(before, factory.Roles.Lookups);
    }

    [Fact]
    public async Task A_client_that_cannot_speak_messagepack_is_refused_rather_than_downgraded()
    {
        // §13.13a. Supporting JSON alongside MessagePack would let a client
        // negotiate it and silently take a 25% worse wire and a §7 cap that
        // admits 49 KB rather than 65 KB of operations — a downgrade nobody
        // would ever see. Failing to connect is loud, and §13.13 is why loud is
        // the better failure.
        //
        // Every other hub test in this file registers the MessagePack protocol.
        // This one deliberately does not, which is the whole assertion.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        var (_, _, negotiated) = await JoinAsync(factory, "json-client", Role.Editor);

        var server = factory.Server;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, $"/hub/editor?access_token={Uri.EscapeDataString(negotiated.Ticket)}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                        await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
                })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(
            () => connection.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hub_errors_never_carry_server_exception_detail()
    {
        // Detailed errors append the exception's type and message to every hub
        // failure and send it to whoever is connected. That is server internals
        // to any client, and it also drowns §7's codes in prose. It defaults on
        // in Development, which is exactly the environment where someone would
        // notice least.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);
        using var client = factory.CreateClient();

        var options = factory.Services
            .GetRequiredService<IOptions<HubOptions>>().Value;

        Assert.False(options.EnableDetailedErrors);
    }

    /// <summary>
    /// Asserts a refusal carries exactly one code and nothing else.
    /// </summary>
    private static void AssertRejected(string expected, SubmitResult result)
    {
        // Equality, not Contains: the code reaches the client as-is, and
        // anything appended to it — a document id, a role, a reason — is the
        // leak §7's 404 rule exists to prevent.
        Assert.Equal(expected, result.Code);
        Assert.Equal(0, result.Accepted);
    }

    private static Task<SubmitResult> SubmitAsync(
        HubConnection connection, Guid documentId, Guid replicaId, byte[]? operations = null) =>
        connection.InvokeAsync<SubmitResult>(
            "SubmitAsync",
            new OperationBatchMessage(
                documentId, replicaId, operations ?? new ReplicaWriter(replicaId).Type("a")),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A hub connection over WebSockets, which is what a browser uses.
    /// </summary>
    /// <remarks>
    /// The transport matters here rather than being an incidental choice. A
    /// connection refused in <c>OnConnectedAsync</c> fails the WebSocket
    /// handshake, so the client's StartAsync throws and a refusal is
    /// unambiguous. Under long polling the same refusal arrives after the
    /// client already believes it is connected, which would make every test
    /// below assert "started, then closed shortly afterwards" — a race, and a
    /// weaker claim than the one §7 is making.
    /// </remarks>
    private static HubConnection Connect(EditorApiFactory factory, string? ticket)
    {
        var query = ticket is null ? string.Empty : $"?access_token={Uri.EscapeDataString(ticket)}";
        var server = factory.Server;

        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, $"/hub/editor{query}"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var client = server.CreateWebSocketClient();
                    return await client.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddMessagePackProtocol()
            .Build();
    }

    /// <summary>Creates a document, grants a role on it, and negotiates.</summary>
    private static async Task<(Guid DocumentId, Guid UserId, Negotiated Negotiated)> JoinAsync(
        EditorApiFactory factory, string subject, Role role)
    {
        var ownerSubject = "owner-" + subject;
        var owner = await factory.CreateUserAsync(ownerSubject, TestContext.Current.CancellationToken);
        var userId = await factory.CreateUserAsync(subject, TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
                .SetRoleAsync(documentId, userId, role, owner, TestContext.Current.CancellationToken);
        }

        using var client = factory.ClientFor(subject);
        using var response = await client.PostAsync(
            new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
            null,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var negotiated = await response.Content.ReadFromJsonAsync<Negotiated>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(negotiated);
        return (documentId, userId, negotiated);
    }
}
