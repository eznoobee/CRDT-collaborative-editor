using Crdt.Core;
using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Catch-up by version vector (§8).
/// </summary>
/// <remarks>
/// A reconnecting client says what it holds and the server works out the rest.
/// The cursor is the version vector rather than <c>server_seq</c> because §8
/// makes broadcast unordered: a client can hold 105 without holding 100, and a
/// watermark would skip whatever fell in the gap.
/// <para>
/// The vacuity risks named before these were written. First: every one of these
/// tests ends in convergence, and convergence is a weak assertion (§13.15) —
/// a server that answered every catch-up with a full snapshot would converge
/// every client and pass on that basis alone. So each test asserts which path
/// the server took, directly, and the delta tests would fail against a
/// snapshot-always server. Second: a client that is not actually behind is
/// caught up by a server that returns nothing, so the client here misses
/// operations it genuinely received and deliberately did not apply. Third: the
/// snapshot floor is unreachable at any size these tests would otherwise run
/// at, so it is exercised on its own with the delta path switched off (§13.14)
/// and separately reached through the cap the way production would.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class CatchUpTests
{
    private readonly EditorFixture _fixture;

    public CatchUpTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_client_that_missed_operations_is_given_them_as_a_delta()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-delta");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-delta", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-delta", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-delta", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-delta", documentId);

        // Applied by the reader, so its vector genuinely covers them.
        var seen = writer.Writer.Type("seen");
        Assert.Null((await writer.SubmitAsync(seen)).Code);
        writer.ApplyLocal(seen);
        reader.Apply(await reader.NextAsync());

        // Received and deliberately not applied: the reader is behind by
        // exactly these, which is what makes the answer below non-trivial.
        var missed = writer.Writer.Type("missed");
        Assert.Null((await writer.SubmitAsync(missed)).Code);
        writer.ApplyLocal(missed);
        await reader.NextAsync();

        var result = await reader.CatchUpAsync();

        Assert.Null(result.Code);

        // The path, asserted rather than inferred. A snapshot here would also
        // converge, and would mean every reconnect on a busy document paid for
        // the whole thing.
        Assert.Null(result.Snapshot);
        Assert.Equal(6, OperationBinary.Decode(result.Operations).Count);

        reader.ApplyCatchUp(result);

        Assert.Equal(0, reader.Replica.PendingCount);
        Assert.Equal("seenmissed", reader.Replica.Text);
        Assert.Equal(writer.Normalised, reader.Normalised);
    }

    [Fact]
    public async Task The_cursor_is_the_version_vector_rather_than_the_highest_server_seq()
    {
        // The reason §8 forbids a watermark. The watcher holds an operation
        // with a *higher* server_seq than the one it is missing, so a server
        // that answered "everything after your highest server_seq" would
        // return nothing and leave the watcher permanently short one insert —
        // converging with nobody, silently, on a document that still renders.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-cursor");
        await DocumentSetup.GrantAsync(factory, documentId, "first-cursor", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "second-cursor", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "watcher-cursor", Role.Editor);

        await using var first = await DocumentClient.JoinAsync(factory, "first-cursor", documentId);
        await using var second = await DocumentClient.JoinAsync(factory, "second-cursor", documentId);
        await using var watcher = await DocumentClient.JoinAsync(factory, "watcher-cursor", documentId);

        var earlier = first.Writer.Type("F");
        Assert.Null((await first.SubmitAsync(earlier)).Code);
        first.ApplyLocal(earlier);

        var fromFirst = await watcher.NextAsync();
        second.Apply(await second.NextAsync());

        var later = second.Writer.Type("S");
        Assert.Null((await second.SubmitAsync(later)).Code);
        second.ApplyLocal(later);
        first.Apply(await first.NextAsync());

        // The hole. The watcher applies only the later operation, so its
        // highest server_seq is above the gap rather than below it.
        watcher.Apply(await watcher.NextAsync());
        Assert.Equal(1, watcher.Replica.Text.Length);
        Assert.True(fromFirst.ServerSeq < watcher.Received[^1].ServerSeq);

        var result = await watcher.CatchUpAsync();

        Assert.Null(result.Code);
        Assert.Null(result.Snapshot);

        var recovered = OperationBinary.Decode(result.Operations);
        Assert.Equal(
            [ReplicaIdConversion.FromGuid(first.Negotiated.ReplicaId)],
            recovered.Select(operation => operation.Id.Replica).Distinct());

        watcher.ApplyCatchUp(result);

        Assert.Equal(0, watcher.Replica.PendingCount);
        Assert.Equal(2, watcher.Replica.Text.Length);
        Assert.Equal(first.Normalised, watcher.Normalised);
        Assert.Equal(second.Normalised, watcher.Normalised);
    }

    [Fact]
    public async Task A_client_that_is_already_current_is_sent_nothing()
    {
        // The boundary. The vector states the next sequence number expected,
        // not the last one held, so a server comparing with the wrong
        // inequality re-sends the client's most recent operation on every
        // reconnect — harmless by idempotency and therefore invisible, until
        // it is a full document's worth on a reconnect storm.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-current");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-current", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-current", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-current", documentId);
        await using var reader = await DocumentClient.JoinAsync(factory, "reader-current", documentId);

        var batch = writer.Writer.Type("abc");
        Assert.Null((await writer.SubmitAsync(batch)).Code);
        writer.ApplyLocal(batch);
        reader.Apply(await reader.NextAsync());

        var result = await reader.CatchUpAsync();

        Assert.Null(result.Code);
        Assert.Null(result.Snapshot);
        Assert.Empty(OperationBinary.Decode(result.Operations));
    }

    [Fact]
    public async Task The_snapshot_floor_is_exercised_with_the_delta_path_switched_off()
    {
        // §13.14. Left alone, this path runs only above two thousand
        // operations, which is a size no test here reaches — so the fallback
        // would sit behind a working fast path, never executed, until the first
        // real document large enough to need it. Switching the fast path off is
        // the only way to find out whether the floor works.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-floor");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-floor", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "joiner-floor", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-floor", documentId);

        var batch = writer.Writer.Type("floor");
        Assert.Null((await writer.SubmitAsync(batch)).Code);
        writer.ApplyLocal(batch);

        var deleted = writer.Writer.Delete(0);
        Assert.Null((await writer.SubmitAsync(deleted)).Code);
        writer.ApplyLocal(deleted);

        // Joined after the writing, holding nothing at all — the state a client
        // is in when its local storage is gone.
        await using var joiner = await DocumentClient.JoinAsync(factory, "joiner-floor", documentId);
        Assert.Empty(joiner.Replica.AllIds);

        var result = await joiner.CatchUpAsync(forceSnapshot: true);

        Assert.Null(result.Code);
        Assert.NotNull(result.Snapshot);
        Assert.Empty(OperationBinary.Decode(result.Operations));

        joiner.ApplyCatchUp(result);

        // Compared on §9's normalised form rather than on the text, because the
        // deleted element is a tombstone the text does not show and a snapshot
        // that dropped it would still read "loor".
        Assert.Equal("loor", joiner.Replica.Text);
        Assert.Equal(writer.Normalised, joiner.Normalised);
    }

    [Fact]
    public async Task A_delta_past_the_cap_is_answered_with_a_snapshot_instead()
    {
        // The floor reached the way production reaches it, through the cap
        // rather than through the test-only switch above. Without this, the
        // configured threshold would be a number nothing consults.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(
            _fixture,
            settings: new Dictionary<string, string?> { ["CatchUp:MaxDeltaOperations"] = "4" });

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-cap");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-cap", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "joiner-cap", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(factory, "writer-cap", documentId);
        await using var joiner = await DocumentClient.JoinAsync(factory, "joiner-cap", documentId);

        var under = writer.Writer.Type("abcd");
        Assert.Null((await writer.SubmitAsync(under)).Code);
        writer.ApplyLocal(under);
        await joiner.NextAsync();

        // Exactly at the cap: still a delta. The other half of the assertion —
        // without it, "answers with a snapshot" is satisfied by a server that
        // never sends a delta at all, and the cap is again a number nothing
        // consults.
        var atCap = await joiner.CatchUpAsync();
        Assert.Null(atCap.Snapshot);
        Assert.Equal(4, OperationBinary.Decode(atCap.Operations).Count);

        var over = writer.Writer.Type("e");
        Assert.Null((await writer.SubmitAsync(over)).Code);
        writer.ApplyLocal(over);
        await joiner.NextAsync();

        var pastCap = await joiner.CatchUpAsync();

        Assert.NotNull(pastCap.Snapshot);
        Assert.Empty(OperationBinary.Decode(pastCap.Operations));

        joiner.ApplyCatchUp(pastCap);

        Assert.Equal("abcde", joiner.Replica.Text);
        Assert.Equal(writer.Normalised, joiner.Normalised);
    }

    [Fact]
    public async Task Catch_up_is_refused_once_the_role_is_revoked()
    {
        // Catch-up hands back the whole document, so it is a read path and has
        // to be authorized like one. A version that checked only the connection
        // binding would let a removed collaborator keep reading the document
        // for as long as they held the socket open — and unlike a submission,
        // reading leaves nothing behind to notice.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-revoked-catchup");
        await DocumentSetup.GrantAsync(factory, documentId, "revoked-catchup", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "revoked-catchup", documentId);

        // Allowed first, so the refusal below is the revocation and not some
        // standing failure of this call.
        Assert.Null((await client.CatchUpAsync()).Code);

        var userId = await factory.CreateUserAsync(
            "revoked-catchup", TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
                .RemoveAsync(documentId, userId, TestContext.Current.CancellationToken);
        }

        var result = await client.CatchUpAsync();

        Assert.Equal(HubErrors.NotFound, result.Code);
        Assert.Null(result.Snapshot);
        Assert.Empty(result.Operations);
    }

    [Fact]
    public async Task A_negative_sequence_number_is_rejected_rather_than_wrapped()
    {
        // The vector crosses the wire as signed and is held unsigned. An
        // unchecked cast turns -1 into 18,446,744,073,709,551,615, which no
        // operation is ever past, so the client is told it is current and is
        // handed nothing — a client that can silently freeze its own document
        // by sending one negative number.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-negative");
        await DocumentSetup.GrantAsync(factory, documentId, "negative", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "negative", documentId);

        var batch = client.Writer.Type("abc");
        Assert.Null((await client.SubmitAsync(batch)).Code);
        client.ApplyLocal(batch);

        var result = await client.CatchUpAsync(
            new Dictionary<Guid, long> { [client.Negotiated.ReplicaId] = -1 });

        Assert.Equal(IngestRejection.Malformed, result.Code);
        Assert.Null(result.Snapshot);
        Assert.Empty(result.Operations);
    }
}
