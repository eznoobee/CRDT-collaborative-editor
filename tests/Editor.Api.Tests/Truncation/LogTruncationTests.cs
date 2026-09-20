using System.Text;
using Crdt.Core;
using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Editor.Api.Tests.Truncation;

/// <summary>
/// Collection's second half: the rows, after the elements (register row 30).
/// </summary>
/// <remarks>
/// <para>
/// §5's collection shrinks the stored snapshot and leaves the log alone, so
/// until now nothing had actually been reclaimed — and, separately, the state
/// <c>resync_required</c> detects could not arise from any client action,
/// because an id below the frontier from a known author was always still in the
/// log. Both of those are this file.
/// </para>
/// <para>
/// The vacuity risks, named before these were written.
/// </para><para>
/// <b>First, and the one that destroys data: "rows went" is satisfied by
/// removing the wrong rows.</b> A truncator that deleted a prefix would pass
/// every count assertion here and would tell clients to throw away their work
/// for characters that are still on screen — <c>LogTruncationHazardTests</c>
/// holds that scenario. So the first test below is not about counts at all: it
/// types, truncates, and requires the live elements to still resolve.
/// </para><para>
/// <b>Second: <c>resync_required</c> is reachable now, so a test for it must
/// not construct the frontier.</b> Every test of the classifier before this one
/// wrote the frontier directly and said so, because nothing else could reach
/// the state. This one reaches it the way the system does — type, delete a
/// trailing run, let the tab close, retire, collect, truncate — and if any link
/// in that chain stops working the test goes red rather than quietly testing a
/// hand-built row.
/// </para><para>
/// <b>Third: a truncation test that calls the truncator proves the truncator.</b>
/// §12's question 2. The sweep is a background service, and a service nobody
/// registered looks exactly like one that ran and found nothing, so one test
/// moves the clock and requires the rows to go with nobody calling anything.
/// </para><para>
/// <b>Fourth: a count of removed rows is not a reclamation.</b> The rows are
/// counted in the database before and after rather than taken from the
/// truncator's return value, which is the number it believes it removed
/// (§13.44).
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class LogTruncationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly EditorFixture _fixture;

    public LogTruncationTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Truncation_leaves_every_live_element_resolvable()
    {
        // FIRST, DELIBERATELY. This is the property a prefix truncation breaks,
        // and breaking it is how truncation destroys a user's work. Nothing
        // below matters if this does not hold.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var (documentId, author) = await CollectableDocumentAsync(factory, "owner-live", "typist-live");

        Assert.True(await CollectAsync(factory, documentId) > 0, "nothing was collected");
        await TruncateAsync(factory, documentId);

        // "collect me please" with the last eight deleted leaves "collect m"
        // visible: nine live elements, every one of them legitimately
        // referenceable by anyone who holds the document.
        await DocumentSetup.GrantAsync(factory, documentId, "live-peer", Role.Editor);
        await using var peer = await DocumentClient.JoinAsync(factory, "live-peer", documentId);

        var referencing = new Peer(peer);
        for (ulong seq = 0; seq <= 8; seq++)
        {
            // Not merely "not resync_required": a live element must resolve
            // outright. Accepting nothing while refusing with a gentler code
            // would still be a document nobody can edit.
            Assert.Null(await referencing.ReferenceAsync(new ElementId(author, seq)));
        }
    }

    [Fact]
    public async Task A_collected_element_stops_resolving_and_earns_resync_required()
    {
        // THE END-TO-END TEST row 30 was opened for. Every assertion in
        // ResyncRequiredTests writes the frontier by hand and says so; this one
        // reaches the state through the product and nothing here touches a
        // frontier, a snapshot or a row.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var (documentId, author) = await CollectableDocumentAsync(factory, "owner-e2e", "typist-e2e");

        // Seq 16 is the last character typed and the last of the deleted run —
        // the tail, which rule 4 collects and rule 4 keeps its leader from.
        var collectedId = new ElementId(author, 16);

        await DocumentSetup.GrantAsync(factory, documentId, "e2e-peer", Role.Editor);
        await using var peer = await DocumentClient.JoinAsync(factory, "e2e-peer", documentId);

        // The peer catches up and says so. Not bookkeeping: a live replica that
        // has acknowledged nothing holds the frontier at zero, and without this
        // the collection below silently collects nothing and every assertion
        // after it passes for the wrong reason.
        peer.ApplyCatchUp(await peer.CatchUpAsync());
        await peer.AcknowledgeAsync();

        // BEFORE. The element is a tombstone, still in the log, and a reference
        // to it resolves. Without this the test would pass against a server
        // that answered resync_required to everything (§13.19).
        var referencing = new Peer(peer);

        // NOTHING REFERENCES THE RUN BEFORE IT IS COLLECTED, and that is not an
        // oversight in the ordering. An insert naming seq 16 becomes its right
        // child, and §5's rule 2 never collects a node with children — so a
        // "before" probe against the element under test would quietly make it
        // uncollectable and every assertion after it would fail for a reason
        // that has nothing to do with truncation. It cost a debugging cycle to
        // find, which is why it is written down here.
        //
        // What the probe was for — proving the server does not simply answer
        // resync_required to everything — is covered by the assertion below,
        // taken after collection and before truncation.
        Assert.True(await CollectAsync(factory, documentId) > 0, "nothing was collected");

        // Collection alone is not enough, and that is the finding row 30
        // records: the snapshot shrank, the log did not, and the reference
        // still resolves. This is also the non-vacuity check — a server that
        // answered resync_required unconditionally fails here.
        Assert.Null(await referencing.ReferenceAsync(collectedId));

        await TruncateAsync(factory, documentId);

        // AFTER. The row is gone, the element is gone, and the server can now
        // say what §9 wrote the code for: this existed and was collected.
        Assert.Equal(
            IngestRejection.ResyncRequired,
            await referencing.ReferenceAsync(collectedId));
    }

    [Fact]
    public async Task The_sweep_reclaims_rows_on_its_own_timer_without_anyone_calling_it()
    {
        // §12's question 2. Every other test in this file calls the truncator
        // and would pass with the hosted service unregistered — which is the
        // state a truncation subsystem can sit in indefinitely, because a sweep
        // that never runs and a sweep that finds nothing produce the same
        // counters and the same disk usage right up until they do not.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var (documentId, _) = await CollectableDocumentAsync(factory, "owner-sweep", "typist-sweep");
        Assert.True(await CollectAsync(factory, documentId) > 0, "nothing was collected");

        var before = await LogRowsAsync(factory, documentId);
        var job = factory.Services.GetRequiredService<LogTruncationSweeper>();
        Assert.Equal(0, job.Sweeps);

        // Advanced repeatedly rather than once. A sweep examines a bounded
        // batch and this database holds every other test's documents, so one
        // tick is not guaranteed to reach this one — the rotation
        // Document.LastTruncatedAt exists for, exercised as a side effect
        // rather than assumed.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var after = before;
        while (DateTime.UtcNow < deadline && after == before)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            after = await LogRowsAsync(factory, documentId);
        }

        Assert.True(
            after < before,
            $"the sweep never reclaimed anything: {before} rows before, {after} after, "
                + $"{job.Sweeps} sweeps.");

        Assert.True(job.Sweeps > 0);
        Assert.True(job.Removed > 0);
    }

    [Fact]
    public async Task Truncation_removes_only_what_collection_collected()
    {
        // The count, read from the database on both sides rather than from what
        // the truncator says it did. Seventeen characters typed, eight deleted,
        // seven collected — so seven insert rows go and every other row stays,
        // the eight delete rows included.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var (documentId, _) = await CollectableDocumentAsync(factory, "owner-count", "typist-count");

        var before = await LogRowsAsync(factory, documentId);
        Assert.Equal(25, before);

        var collected = await CollectAsync(factory, documentId);
        Assert.Equal(7, collected);

        await TruncateAsync(factory, documentId);

        var after = await LogRowsAsync(factory, documentId);
        Assert.Equal(before - collected, after);

        // And the delete rows are all still there, which is the cost
        // LogTruncator takes deliberately so that §9's "that id names a delete"
        // narrowing keeps working.
        Assert.Equal(8, await LogRowsAsync(factory, documentId, OperationMapper.DeleteType));
    }

    [Fact]
    public async Task A_document_with_no_snapshot_is_never_truncated()
    {
        // The refusal that makes truncation safe to run on anything. Without a
        // snapshot there is no verified state to fall back on, so there is
        // nothing here that can be given up — and a truncator that treated
        // "no snapshot" as "nothing to keep" would empty the log.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-nosnap");
        await DocumentSetup.GrantAsync(factory, documentId, "nosnap", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "nosnap", documentId))
        {
            Assert.Null((await client.SubmitAsync(client.Writer.Type("no snapshot here"))).Code);
        }

        var before = await LogRowsAsync(factory, documentId);
        Assert.True(before > 0);

        Assert.Equal(0, await TruncateAsync(factory, documentId));
        Assert.Equal(before, await LogRowsAsync(factory, documentId));
    }

    [Fact]
    public async Task A_truncated_document_still_loads_as_itself()
    {
        // The reconstruction, which is what the rows were being kept for. §5
        // requires collection to be invisible through the product; truncation
        // is where that stops being a statement about a cache.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var (documentId, _) = await CollectableDocumentAsync(factory, "owner-load", "typist-load");

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DocumentStore>();
        var asReplica = ReplicaIdConversion.FromGuid(documentId);

        var before = await store.LoadAsync(documentId, asReplica, TestContext.Current.CancellationToken);
        var text = before.Text;
        Assert.Equal("collect m", text);

        Assert.True(await CollectAsync(factory, documentId) > 0);
        await TruncateAsync(factory, documentId);

        var after = await store.LoadAsync(documentId, asReplica, TestContext.Current.CancellationToken);
        Assert.Equal(text, after.Text);
    }

    /// <summary>
    /// Submits an insert naming <paramref name="parent"/>, as the peer.
    /// </summary>
    /// <remarks>
    /// The sequence number advances only when the server accepts. §5 makes the
    /// per-replica sequence dense, and a rejected batch is not written — so
    /// incrementing past a refusal would make every later submission a
    /// <c>sequence_gap</c> and turn the next assertion into a test of this
    /// helper's arithmetic.
    /// </remarks>
    private sealed class Peer(DocumentClient client)
    {
        private ulong _seq;

        public async Task<string?> ReferenceAsync(ElementId parent)
        {
            var batch = OperationBinary.Encode(
            [
                new InsertOperation(
                    new ElementId(ReplicaIdConversion.FromGuid(client.Negotiated.ReplicaId), _seq),
                    new Rune('!'),
                    parent,
                    Side.Right,
                    null),
            ]);

            var result = await client.SubmitAsync(batch);
            if (result.Code is null)
            {
                _seq++;
            }

            return result.Code;
        }
    }

    private static async Task<int> CollectAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISnapshotGarbageCollector>()
            .CollectDocumentAsync(documentId, TestContext.Current.CancellationToken);
    }

    private static async Task<int> TruncateAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ILogTruncator>()
            .TruncateDocumentAsync(documentId, TestContext.Current.CancellationToken);
    }

    /// <summary>Rows the log holds for a document, optionally of one type.</summary>
    private static async Task<int> LogRowsAsync(
        EditorApiFactory factory, Guid documentId, string? opType = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        return await context.DocumentOperations
            .Where(row => row.DocumentId == documentId && (opType == null || row.OpType == opType))
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private static EditorApiFactory Frozen(EditorFixture fixture, FakeTimeProvider clock) =>
        new(fixture, configure: services => services.AddSingleton<TimeProvider>(clock));

    /// <summary>
    /// A document left in the state collection exists for, built entirely
    /// through the product's own path.
    /// </summary>
    private static async Task<(Guid DocumentId, ReplicaId Author)> CollectableDocumentAsync(
        EditorApiFactory factory, string owner, string typist)
    {
        var documentId = await DocumentSetup.DocumentAsync(factory, owner);
        await DocumentSetup.GrantAsync(factory, documentId, typist, Role.Editor);

        ReplicaId author;
        await using (var client = await DocumentClient.JoinAsync(factory, typist, documentId))
        {
            author = ReplicaIdConversion.FromGuid(client.Negotiated.ReplicaId);
            var typed = client.Writer.Type("collect me please");
            Assert.Null((await client.SubmitAsync(typed)).Code);
            client.ApplyLocal(typed);

            // A consecutive run at the END of the text. A mid-document run
            // collects nothing — forward typing makes every character the right
            // child of the one before it, so an interior tombstone is never a
            // leaf (row 29).
            for (ulong seq = 9; seq <= 16; seq++)
            {
                var deleted = client.Writer.Delete(seq);
                Assert.Null((await client.SubmitAsync(deleted)).Code);
                client.ApplyLocal(deleted);
            }

            await client.AcknowledgeAsync();
        }

        // The tab closes. Retirement is what lets the frontier move past it.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.DocumentReplicas
                .Where(row => row.DocumentId == documentId)
                .ExecuteUpdateAsync(
                    row => row.SetProperty(r => r.RetiredAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        return (documentId, author);
    }
}
