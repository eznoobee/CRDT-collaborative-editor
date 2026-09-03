using System.Net.Http.Json;
using System.Threading.Channels;
using Crdt.Core;
using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>What `negotiate` hands back.</summary>
public sealed record Negotiated(
    string Ticket, Guid DocumentId, Guid ReplicaId, Role Role, bool Resumed);

/// <summary>
/// One connected editor: a negotiated ticket, a live hub connection, a sequence
/// of its own, and everything the server has broadcast to it.
/// </summary>
/// <remarks>
/// Shared by every multi-client test from 3b.2 on. A second helper would drift
/// from this one, and the tests that matter most in this phase are the ones
/// where two clients have to agree — which is exactly the property a divergent
/// pair of test helpers would quietly undermine.
/// </remarks>
public sealed class DocumentClient : IAsyncDisposable
{
    private readonly Channel<OperationBroadcast> _received =
        Channel.CreateUnbounded<OperationBroadcast>();

    private readonly List<OperationBroadcast> _all = [];

    private DocumentClient(HubConnection connection, Negotiated negotiated)
    {
        Connection = connection;
        Negotiated = negotiated;
        Writer = new ReplicaWriter(negotiated.ReplicaId);
        Replica = new Replica(ReplicaIdConversion.FromGuid(negotiated.ReplicaId));
    }

    public HubConnection Connection { get; }

    public Negotiated Negotiated { get; }

    /// <summary>Produces this client's own operations, in sequence.</summary>
    public ReplicaWriter Writer { get; }

    /// <summary>
    /// This client's own copy of the document, as a real replica.
    /// </summary>
    /// <remarks>
    /// Operations are applied into it explicitly by the test rather than
    /// automatically on arrival, because the order in which a test applies them
    /// is the variable under test: §8 makes broadcast unordered, so a test that
    /// applied in arrival order would only ever exercise the order this
    /// particular run happened to produce.
    /// </remarks>
    public Replica Replica { get; private set; }

    /// <summary>§9's normalised form of what this client believes the document is.</summary>
    public string Normalised => SnapshotSerializer.Serialize(Replica);

    /// <summary>Decodes a broadcast and applies it to <see cref="Replica"/>.</summary>
    public void Apply(OperationBroadcast broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);

        foreach (var operation in OperationBinary.Decode(broadcast.Operations))
        {
            Replica.Apply(operation);
        }
    }

    /// <summary>Applies this client's own batch, as a real client would locally.</summary>
    public void ApplyLocal(byte[] operations)
    {
        foreach (var operation in OperationBinary.Decode(operations))
        {
            Replica.Apply(operation);
        }
    }

    /// <summary>Asks the server what this client has missed.</summary>
    /// <remarks>
    /// The vector sent is this replica's own, not a record of what the test
    /// delivered: the point of catch-up is that the client states what it holds
    /// and the server works out the rest, so a test that computed the gap
    /// itself would be testing its own arithmetic.
    /// </remarks>
    public Task<CatchUpResult> CatchUpAsync(bool forceSnapshot = false) =>
        Connection.InvokeAsync<CatchUpResult>(
            "CatchUpAsync",
            Replica.VersionVector.ToDictionary(
                entry => ReplicaIdConversion.ToGuid(entry.Key),
                entry => (long)entry.Value),
            forceSnapshot,
            TestContext.Current.CancellationToken);

    /// <summary>Sends a version vector the client would never produce itself.</summary>
    public Task<CatchUpResult> CatchUpAsync(Dictionary<Guid, long> known, bool forceSnapshot = false) =>
        Connection.InvokeAsync<CatchUpResult>(
            "CatchUpAsync", known, forceSnapshot, TestContext.Current.CancellationToken);

    /// <summary>Adopts a catch-up answer the way a reconnecting client would.</summary>
    /// <remarks>
    /// A snapshot replaces the replica outright — it is the state, not an
    /// addition to it — and the operations are applied on top in either case,
    /// because §8 allows the tail to arrive with the snapshot.
    /// </remarks>
    public void ApplyCatchUp(CatchUpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Snapshot is not null)
        {
            Replica = SnapshotBinary.Decode(
                ReplicaIdConversion.FromGuid(Negotiated.ReplicaId), result.Snapshot);
        }

        foreach (var operation in OperationBinary.Decode(result.Operations))
        {
            Replica.Apply(operation);
        }
    }

    /// <summary>Everything received so far, in arrival order.</summary>
    public IReadOnlyList<OperationBroadcast> Received
    {
        get
        {
            lock (_all)
            {
                return [.. _all];
            }
        }
    }

    /// <summary>Negotiates, connects, and starts listening.</summary>
    public static async Task<DocumentClient> JoinAsync(
        EditorApiFactory factory, string subject, Guid documentId, Guid? resume = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var http = factory.ClientFor(subject);
        using var response = await http.PostAsJsonAsync(
            new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
            new { replicaId = resume },
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var negotiated = await response.Content.ReadFromJsonAsync<Negotiated>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(negotiated);

        var server = factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(server.BaseAddress, $"/hub/editor?access_token={Uri.EscapeDataString(negotiated.Ticket)}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                        await server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
                })
            .AddMessagePackProtocol()
            .Build();

        var client = new DocumentClient(connection, negotiated);

        connection.On<OperationBroadcast>(EditorHub.Broadcast, broadcast =>
        {
            lock (client._all)
            {
                client._all.Add(broadcast);
            }

            client._received.Writer.TryWrite(broadcast);
        });

        await connection.StartAsync(TestContext.Current.CancellationToken);
        return client;
    }

    /// <summary>Submits a batch; defaults to typing one more character.</summary>
    public Task<SubmitResult> SubmitAsync(byte[]? operations = null) =>
        Connection.InvokeAsync<SubmitResult>(
            "SubmitAsync",
            new OperationBatchMessage(
                Negotiated.DocumentId, Negotiated.ReplicaId, operations ?? Writer.Type("a")),
            TestContext.Current.CancellationToken);

    /// <summary>Submits into a document or as a replica this client is not bound to.</summary>
    public Task<SubmitResult> SubmitAsAsync(Guid documentId, Guid replicaId, byte[] operations) =>
        Connection.InvokeAsync<SubmitResult>(
            "SubmitAsync",
            new OperationBatchMessage(documentId, replicaId, operations),
            TestContext.Current.CancellationToken);

    /// <summary>Waits for the next broadcast, or fails the test.</summary>
    public async Task<OperationBroadcast> NextAsync(TimeSpan? within = null)
    {
        using var timeout = new CancellationTokenSource(within ?? TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);

        try
        {
            return await _received.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("No broadcast arrived within the timeout.");
            throw;
        }
    }

    /// <summary>
    /// Asserts nothing arrives for <paramref name="within"/>.
    /// </summary>
    /// <remarks>
    /// A negative with a real wait, because the alternative — checking a list
    /// immediately after another client submits — passes whether or not the
    /// server would eventually send, and would keep passing if fan-out were
    /// removed entirely.
    /// </remarks>
    public async Task NothingArrivesAsync(TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);

        try
        {
            var unexpected = await _received.Reader.ReadAsync(timeout.Token);
            Assert.Fail($"A broadcast arrived that should not have: server_seq {unexpected.ServerSeq}.");
        }
        catch (OperationCanceledException)
        {
            // Nothing arrived, which is the assertion.
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

/// <summary>Document and membership setup shared by the multi-client tests.</summary>
public static class DocumentSetup
{
    /// <summary>Creates a document owned by <paramref name="ownerSubject"/>.</summary>
    public static async Task<Guid> DocumentAsync(EditorApiFactory factory, string ownerSubject)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var owner = await factory.CreateUserAsync(ownerSubject, TestContext.Current.CancellationToken);
        return await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Grants <paramref name="subject"/> a role on the document.</summary>
    public static async Task GrantAsync(
        EditorApiFactory factory, Guid documentId, string subject, Role role)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var userId = await factory.CreateUserAsync(subject, TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
            .SetRoleAsync(documentId, userId, role, userId, TestContext.Current.CancellationToken);
    }
}
