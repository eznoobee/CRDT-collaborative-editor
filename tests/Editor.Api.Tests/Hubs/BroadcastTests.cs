using Crdt.Core;
using Editor.Domain;
using Editor.Infrastructure.Serialization;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Fan-out to the other connections on a document (§8).
/// </summary>
/// <remarks>
/// <b>These tests are not sufficient on their own and the breakdown says so.</b>
/// Every one of them passes against a single instance whose backplane does
/// nothing at all, because both connections are on the same server and SignalR's
/// in-memory group is enough. What makes fan-out mean anything is the
/// two-instance test in 3b.7; until that exists this task is not verified, only
/// written.
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class BroadcastTests
{
    private readonly EditorFixture _fixture;

    public BroadcastTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_submitted_batch_reaches_the_other_connection()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-fanout");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-fanout", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-fanout", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-fanout", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-fanout", documentId);

        var sent = writer.Writer.Type("hello");
        Assert.Null((await writer.SubmitAsync(sent)).Code);

        var received = await reader.NextAsync();

        Assert.Equal(documentId, received.DocumentId);
        Assert.Equal(sent, received.Operations);

        // The bytes are relayed as sent, so the receiver decodes the same
        // operations the sender encoded — runs included. That is the §6
        // constraint holding: the transport moved a byte string and did not
        // re-encode anything.
        var decoded = OperationBinary.Decode(received.Operations);
        Assert.Equal(5, decoded.Count);
        Assert.Equal(
            "hello",
            string.Concat(decoded.Cast<InsertOperation>().Select(op => op.Value.ToString())));
    }

    [Fact]
    public async Task The_sender_does_not_receive_its_own_batch()
    {
        // An optimisation rather than a correctness rule — §5 makes
        // re-delivery harmless — but a hub echoing every batch to its author
        // doubles fan-out for nothing, and nothing else would notice.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-echo");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-echo", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-echo", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-echo", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-echo", documentId);

        Assert.Null((await writer.SubmitAsync()).Code);

        // The reader receiving is what proves the writer's silence is exclusion
        // rather than fan-out being broken.
        await reader.NextAsync();
        await writer.NothingArrivesAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_connection_on_another_document_receives_nothing()
    {
        // Groups are per document. Without this, "it broadcasts" could mean
        // "it broadcasts to everyone", which is both a scaling failure and a
        // disclosure of one document's content to a member of another.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var first = await DocumentSetup.DocumentAsync(factory, "owner-a");
        var second = await DocumentSetup.DocumentAsync(factory, "owner-b");
        await DocumentSetup.GrantAsync(factory, first, "member-both", Role.Editor);
        await DocumentSetup.GrantAsync(factory, second, "member-both", Role.Editor);

        await using var onFirst = await DocumentClient.JoinAsync(factory, "member-both", first);
        await using var onSecond = await DocumentClient.JoinAsync(factory, "member-both", second);

        Assert.Null((await onFirst.SubmitAsync()).Code);

        await onSecond.NothingArrivesAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_rejected_batch_is_not_broadcast()
    {
        // Fan-out happens after the write. A client that received an operation
        // the server then refused would hold state no amount of reconnecting
        // could reconcile, because catch-up reads the log and the log does not
        // have it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-rejected");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-rejected", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-rejected", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-rejected", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-rejected", documentId);

        // A sequence gap: structurally valid, refused by ingest.
        var gapped = new ReplicaWriter(writer.Negotiated.ReplicaId, startSeq: 5).Type("x");
        Assert.NotNull((await writer.SubmitAsync(gapped)).Code);

        await reader.NothingArrivesAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task The_broadcast_carries_a_server_seq_that_advances()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-seq");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-seq", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-seq", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-seq", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-seq", documentId);

        Assert.Null((await writer.SubmitAsync(writer.Writer.Type("a"))).Code);
        var first = await reader.NextAsync();

        Assert.Null((await writer.SubmitAsync(writer.Writer.Type("b"))).Code);
        var second = await reader.NextAsync();

        Assert.True(
            second.ServerSeq > first.ServerSeq,
            $"server_seq did not advance: {first.ServerSeq} then {second.ServerSeq}.");
    }
}
