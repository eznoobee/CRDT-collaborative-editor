using Crdt.Core;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Truncation;

/// <summary>
/// What a client catching up over a truncated log receives.
/// </summary>
/// <remarks>
/// <para>
/// 7b.8 established that truncation must not remove a prefix, because a live
/// element with no row is indistinguishable from a collected one. It removes
/// only the rows of elements the snapshot no longer holds, and it keeps every
/// delete row so §9's "that id names a delete" narrowing survives.
/// </para><para>
/// <b>That pairing has a consequence 7b.8 did not test.</b> A fresh client
/// catching up sends an empty version vector, which asks for everything, and
/// the server answers with a delta of surviving rows rather than a snapshot.
/// Among those rows are deletes whose targets were truncated away — and §5
/// makes a delete ready only when its target is present, so an unresolvable
/// delete does not fail, it waits. Forever, in the pending set, on a client
/// that believes it is current.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class TruncatedCatchUpTests
{
    private readonly EditorFixture _fixture;

    public TruncatedCatchUpTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_fresh_client_catching_up_over_a_truncated_log_is_not_left_waiting()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await CollectableDocumentAsync(factory, "owner-tcu", "typist-tcu");

        await using var scope = factory.Services.CreateAsyncScope();
        var collector = scope.ServiceProvider.GetRequiredService<ISnapshotGarbageCollector>();
        var truncator = scope.ServiceProvider.GetRequiredService<ILogTruncator>();

        Assert.True(
            await collector.CollectDocumentAsync(documentId, TestContext.Current.CancellationToken) > 0,
            "nothing was collected");

        Assert.True(
            await truncator.TruncateDocumentAsync(documentId, TestContext.Current.CancellationToken) > 0,
            "nothing was truncated");

        // A brand-new client, the way a first open produces one: empty vector,
        // ask for everything.
        await DocumentSetup.GrantAsync(factory, documentId, "fresh-tcu", Role.Editor);
        await using var fresh = await DocumentClient.JoinAsync(factory, "fresh-tcu", documentId);

        var caught = await fresh.CatchUpAsync();
        Assert.Null(caught.Code);

        // A SNAPSHOT, not a delta. Named rather than left implicit: the text
        // assertion below would also pass against a delta that happened to be
        // complete, and the whole point is that over a truncated log it cannot
        // be. A change that silently went back to the delta path turns this red
        // before anyone has to notice a stranded client.
        Assert.NotNull(caught.Snapshot);

        fresh.ApplyCatchUp(caught);

        // The document as everyone else sees it.
        Assert.Equal("collect m", fresh.Replica.Text);

        // And nothing left waiting. A client with a permanently pending
        // operation is not merely untidy: §5 releases the pending set on its
        // dependency arriving, and a dependency that was truncated never
        // arrives, so this client would buffer it for the life of the session
        // while believing it is current.
        Assert.Equal(0, fresh.Replica.PendingCount);
    }

    [Fact]
    public async Task A_document_with_a_snapshot_but_no_truncation_still_gets_a_delta()
    {
        // The other half, and the one that stops the repair from being "serve a
        // snapshot to everybody". §8 is explicit: a client that has been away
        // for one keystroke wants two operations, not a five-megabyte snapshot.
        //
        // THE DOCUMENT HAS TO HAVE A SNAPSHOT, and the first version of this
        // test did not. Without one the safety check returns early on "no
        // snapshot to fall back on" and the delta comes back for a reason that
        // has nothing to do with truncation — so the test passed with the whole
        // never-truncated fast path deleted. The sabotage said so; it is here
        // because a reader would not have.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture, settings: new Dictionary<string, string?>
        {
            ["Snapshots:OperationsPerSnapshot"] = "1",
            ["Snapshots:BatchSize"] = "64",
        });

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-untruncated");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-untruncated", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "reader-untruncated", Role.Editor);

        await using var writer = await DocumentClient.JoinAsync(
            factory, "writer-untruncated", documentId);

        Assert.Null((await writer.SubmitAsync(writer.Writer.Type("hello"))).Code);

        await using (var snapshotScope = factory.Services.CreateAsyncScope())
        {
            Assert.True(
                await snapshotScope.ServiceProvider.GetRequiredService<IPeriodicSnapshotter>()
                    .SnapshotDocumentAsync(documentId, TestContext.Current.CancellationToken),
                "no snapshot was written, so this test would prove nothing");
        }

        // Written after the snapshot, so the reader below is genuinely behind
        // it — which is the state the safety check has to answer, and the state
        // "no snapshot at all" never reaches.
        Assert.Null((await writer.SubmitAsync(writer.Writer.Type(" world"))).Code);

        await using var reader = await DocumentClient.JoinAsync(
            factory, "reader-untruncated", documentId);

        var caught = await reader.CatchUpAsync();

        Assert.Null(caught.Code);
        Assert.Null(caught.Snapshot);

        reader.ApplyCatchUp(caught);
        Assert.Equal("hello world", reader.Replica.Text);
    }

    /// <summary>A document with a collectable trailing run, built through the product.</summary>
    private static async Task<Guid> CollectableDocumentAsync(
        EditorApiFactory factory, string owner, string typist)
    {
        var documentId = await DocumentSetup.DocumentAsync(factory, owner);
        await DocumentSetup.GrantAsync(factory, documentId, typist, Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, typist, documentId))
        {
            var typed = client.Writer.Type("collect me please");
            Assert.Null((await client.SubmitAsync(typed)).Code);
            client.ApplyLocal(typed);

            for (ulong seq = 9; seq <= 16; seq++)
            {
                var deleted = client.Writer.Delete(seq);
                Assert.Null((await client.SubmitAsync(deleted)).Code);
                client.ApplyLocal(deleted);
            }

            await client.AcknowledgeAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.DocumentReplicas
                .Where(row => row.DocumentId == documentId)
                .ExecuteUpdateAsync(
                    row => row.SetProperty(r => r.RetiredAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        return documentId;
    }
}
