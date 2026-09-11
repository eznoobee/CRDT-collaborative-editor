using System.Text;
using Crdt.Core;
using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §5's tombstone collection: the only operation here that destroys data.
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First, and the reason the trigger test is the first test in this file
/// rather than an addition after the rest pass:</b> §13.41 — a test that calls
/// <see cref="ISnapshotGarbageCollector.CollectDocumentAsync"/> proves collection
/// works and says nothing about whether anything ever calls it. GC has that hole
/// available in its most convenient form, because its effect is a count and a
/// shrunk snapshot and both can be had from a direct call. A collector that
/// never runs is indistinguishable from a healthy one until disks fill.
/// </para><para>
/// <b>Second: the §12 question, asked of GC before the code was written.</b> For
/// a mechanism keyed on an action, who is the legitimate user that never
/// performs it? Here it is <i>the offline client</i>. It composes operations
/// against a tree the server cannot see, performs no observable action while
/// away, and is the one whose references collection can invalidate. That is why
/// safety here cannot come from looking at what the server can see: causal
/// stability plus §9's <c>resync_required</c> exist because this user is
/// invisible, and the tests below never treat "no live replica objects" as
/// evidence.
/// </para><para>
/// <b>Third: every convergence assertion in this repository passes over a
/// collector that collects nothing.</b> Zero elements removed is correct,
/// convergent, and useless. Every test here that asserts agreement also asserts
/// a non-zero count, or it is measuring the wrong thing.
/// </para><para>
/// <b>Fourth: comparing text would be comparing convergence, not
/// transparency.</b> §5 requires the §9 normalised form to be identical in every
/// field <i>except</i> <c>elements</c> — <c>elements</c> is the representation
/// and representation is exactly what collection changes, while <c>text</c> and
/// <c>versionVector</c> are the meaning. Comparing the two before a continuation
/// is applied would also prove nothing: collection is only wrong once something
/// new attaches near where the tombstones were.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class TombstoneCollectionTests
{
    private readonly EditorFixture _fixture;

    public TombstoneCollectionTests(EditorFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Now =
        new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_collector_runs_on_its_own_timer_without_anyone_calling_it()
    {
        // FIRST, DELIBERATELY. Nobody calls a collector here: a document is
        // left in a collectable state, the clock moves, and the count has to
        // move on its own. Every other test in this file would pass with the
        // hosted service unregistered.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Frozen(_fixture, clock);

        var documentId = await CollectableDocumentAsync(factory, "owner-timer", "typist-timer");

        var job = factory.Services.GetRequiredService<TombstoneCollector>();
        Assert.Equal(0, job.Sweeps);

        // Time passes repeatedly rather than once. A sweep examines a bounded
        // batch, and this database holds every other test's documents too, so
        // one tick is not guaranteed to reach this one — which is the rotation
        // Document.LastCollectedAt exists for, exercised here as a side effect
        // rather than assumed.
        var elements = await LogElementsAsync(factory, documentId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            // Both, not either. The snapshot is written inside the sweep and
            // the counter is incremented after it returns, so breaking on the
            // shrunken snapshot alone catches the window in between and reads a
            // counter that is still zero — which looks exactly like the defect
            // this test exists to catch.
            var seen = await SnapshotElementsAsync(factory, documentId);
            if (job.Collected > 0 && seen > 0 && seen < elements)
            {
                break;
            }

            clock.Advance(TimeSpan.FromMinutes(6));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var stored = await SnapshotElementsAsync(factory, documentId);

        Assert.True(job.Sweeps > 0, "the collector never swept on its own timer");
        Assert.True(
            job.Collected > 0,
            $"swept {job.Sweeps} times, collected {job.Collected}; "
            + $"snapshot holds {stored} of the log's {elements} elements");

        // And the bytes moved with the count: a counter incremented beside a
        // write that was skipped is the failure this asserts against.
        Assert.True(stored > 0, "no snapshot was written at all");
        Assert.True(
            stored < elements,
            $"the stored snapshot holds {stored} elements and the log produces {elements}");
    }

    [Fact]
    public async Task Collection_removes_causally_stable_tombstones_and_keeps_the_run_leader()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await CollectableDocumentAsync(factory, "owner-direct", "typist-direct");

        var collected = await CollectAsync(factory, documentId);

        // Seventeen characters typed at seqs 0..16, the last eight deleted. The
        // run is eight tombstones and rule 4 keeps its leader, so seven go. The
        // number is asserted rather than ">0" because "collected something" is
        // satisfied by a collector that ignores rule 4 and corrupts the tree.
        Assert.Equal(7, collected);
    }

    [Fact]
    public async Task Collection_reclaims_a_trailing_run_and_not_one_in_the_middle()
    {
        // A LIMIT, PINNED. Forward typing makes each character the right child
        // of the previous one, so a document is a chain. A tombstone in the
        // middle of that chain still has a visible right child hanging off it
        // and can never be a leaf — it stays as a structural placeholder, which
        // is correct and is also the whole of why deleting a word from the
        // middle of a paragraph reclaims nothing.
        //
        // This is here because every collection test in this repository before
        // it deleted from the end, so the suite could not distinguish "GC
        // works" from "GC works on the one shape we happened to test", and 7b
        // would then measure a reclamation rate that does not exist. It is
        // asserted rather than noted so that removing the leaf rule to improve
        // the number turns this red instead of quietly corrupting trees.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var middle = await DocumentSetup.DocumentAsync(factory, "owner-middle");
        await DocumentSetup.GrantAsync(factory, middle, "typist-middle", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "typist-middle", middle))
        {
            var typed = client.Writer.Type("collect me please");
            Assert.Null((await client.SubmitAsync(typed)).Code);
            client.ApplyLocal(typed);

            // The same eight deletions as the trailing case, moved inwards.
            for (ulong seq = 1; seq <= 8; seq++)
            {
                var deleted = client.Writer.Delete(seq);
                Assert.Null((await client.SubmitAsync(deleted)).Code);
                client.ApplyLocal(deleted);
            }

            await client.AcknowledgeAsync();
        }

        await RetireAllAsync(factory, middle);

        Assert.Equal(0, await CollectAsync(factory, middle));
    }

    [Fact]
    public async Task A_sweep_that_collects_nothing_still_reports_that_it_ran()
    {
        // §13.15's pair. Zero collected with zero sweeps is a collector that
        // never ran; zero with sweeps is one with nothing to do. One counter
        // cannot tell them apart, and GC is the subsystem where "reclaimed
        // nothing" is the silent failure.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var job = factory.Services.GetRequiredService<TombstoneCollector>();
        var before = job.Sweeps;

        await job.SweepAsync(TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, job.Sweeps);
    }

    [Fact]
    public async Task A_collected_replica_and_one_that_never_collected_agree_after_a_continuation()
    {
        // THE HEADLINE. §5 requires collection to be transparent: a replica that
        // collected and one that never did must produce the identical §9
        // normalised form in every field except `elements`. `elements` is
        // excluded because representation is exactly what collection changes;
        // `text` and `versionVector` are the meaning, and they may not move.
        //
        // THE CONTINUATION HAS TO BE CONCURRENT, and the first version of this
        // test was not. Generating one operation on the collected replica and
        // applying that same operation to the control is transparent by
        // construction: whatever right origin the collected replica computed,
        // the control simply accepts. Deleting rule 4 outright left that
        // version green. What a right origin is FOR is ordering an insert
        // against a competing one, so the two replicas have to each compose an
        // insert at the same position, from their own view, and then exchange.
        //
        // If the retained leading tombstone were collected, the collected
        // replica would name end-of-document as its right origin while the
        // uncollected one names the tombstone, and the two would interleave the
        // pair differently — permanent divergence, converged, with no error
        // anywhere.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await CollectableDocumentAsync(factory, "owner-headline", "typist-headline");

        var collected = await CollectAsync(factory, documentId);
        Assert.Equal(7, collected);

        // The peer that never collected: a replica that was already online and
        // has no reason to resync, rebuilt from the log with an identity of its
        // own so it can author.
        var uncollected = await ReplayAsync(factory, documentId, Guid.CreateVersion7());

        // A client that joins now and resyncs from a snapshot holds the
        // COLLECTED state — it has never seen the elements that went.
        await DocumentSetup.GrantAsync(factory, documentId, "latecomer", Role.Editor);
        await using var latecomer = await DocumentClient.JoinAsync(factory, "latecomer", documentId);
        latecomer.ApplyCatchUp(await latecomer.CatchUpAsync(forceSnapshot: true));

        Assert.True(
            latecomer.Replica.AllIds.Count < uncollected.AllIds.Count,
            "the latecomer resynced to a snapshot that had not been collected");

        // Both append at the end of the visible text, each from its own view,
        // with no knowledge of the other. This is the position where the
        // collected run was, so it is where a right origin has to agree.
        var fromCollected = latecomer.Replica.Insert(
            latecomer.Replica.Values.Count, new Rune('!'));
        var fromUncollected = uncollected.Insert(uncollected.Values.Count, new Rune('?'));

        Assert.Null((await latecomer.SubmitAsync(OperationBinary.Encode([fromCollected]))).Code);

        // And exchange.
        uncollected.Apply(fromCollected);
        latecomer.Replica.Apply(fromUncollected);

        var collectedForm = SnapshotSerializer.Serialize(latecomer.Replica);
        var controlForm = SnapshotSerializer.Serialize(uncollected);

        // The exclusion is only meaningful if the excluded field actually
        // differs; otherwise this passes over a collector that collected
        // nothing and the comparison proves nothing.
        Assert.NotEqual(Field(collectedForm, "elements"), Field(controlForm, "elements"));

        Assert.Equal(Field(controlForm, "text"), Field(collectedForm, "text"));
        Assert.Equal(Field(controlForm, "v"), Field(collectedForm, "v"));
        Assert.Equal(Field(controlForm, "versionVector"), Field(collectedForm, "versionVector"));
    }

    /// <summary>One field of a §9 normalised form, as its raw JSON.</summary>
    private static string Field(string normalised, string name)
    {
        using var document = System.Text.Json.JsonDocument.Parse(normalised);
        return document.RootElement.GetProperty(name).GetRawText();
    }

    private static async Task<int> CollectAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISnapshotGarbageCollector>()
            .CollectDocumentAsync(documentId, TestContext.Current.CancellationToken);
    }

    /// <summary>A factory whose clock the test drives.</summary>
    private static EditorApiFactory Frozen(EditorFixture fixture, FakeTimeProvider clock) =>
        new(fixture, configure: services =>
            services.AddSingleton<TimeProvider>(clock));

    /// <summary>
    /// A document left in the state collection exists for: a run of causally
    /// stable tombstones, and nobody live who is missing them.
    /// </summary>
    /// <remarks>
    /// Built entirely through the product's own path — a client types, deletes,
    /// acknowledges what it holds, and goes away — rather than by writing rows.
    /// A setup that inserted tombstones directly would be a setup that could not
    /// tell whether the frontier, retirement and acknowledgement path it depends
    /// on actually work.
    /// </remarks>
    private static async Task<Guid> CollectableDocumentAsync(
        EditorApiFactory factory, string owner, string typist)
    {
        var documentId = await DocumentSetup.DocumentAsync(factory, owner);
        await DocumentSetup.GrantAsync(factory, documentId, typist, Role.Editor);

        Guid replicaId;
        await using (var client = await DocumentClient.JoinAsync(factory, typist, documentId))
        {
            replicaId = client.Negotiated.ReplicaId;

            var typed = client.Writer.Type("collect me please");
            Assert.Null((await client.SubmitAsync(typed)).Code);
            client.ApplyLocal(typed);

            // A consecutive run at the END of the text, and the position is not
            // incidental — see
            // Collection_reclaims_a_trailing_run_and_not_one_in_the_middle for
            // why a mid-document run collects nothing. Deleting a single
            // element would also collect nothing: one tombstone is always the
            // leader of its own run, which rule 4 retains.
            for (ulong seq = 9; seq <= 16; seq++)
            {
                var deleted = client.Writer.Delete(seq);
                Assert.Null((await client.SubmitAsync(deleted)).Code);
                client.ApplyLocal(deleted);
            }

            await client.AcknowledgeAsync();
        }

        // The tab closes. Retirement is what lets the frontier move past it,
        // and without that step nothing in this file can collect anything.
        await RetireAllAsync(factory, documentId);
        return documentId;
    }

    private static async Task RetireAllAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        await context.DocumentReplicas
            .Where(row => row.DocumentId == documentId)
            .ExecuteUpdateAsync(
                row => row.SetProperty(r => r.RetiredAt, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
    }

    /// <summary>How many elements the stored snapshot holds.</summary>
    private static async Task<int> SnapshotElementsAsync(
        EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var row = await context.DocumentSnapshots
            .Where(snapshot => snapshot.DocumentId == documentId)
            .OrderByDescending(snapshot => snapshot.ServerSeq)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        if (row is null)
        {
            return 0;
        }

        return SnapshotBinary.Decode(ReplicaIdConversion.FromGuid(documentId), row.State)
            .Export().Count;
    }

    /// <summary>How many elements a full replay of the log produces.</summary>
    private static async Task<int> LogElementsAsync(EditorApiFactory factory, Guid documentId)
    {
        var replica = await ReplayAsync(factory, documentId);
        return replica.Export().Count;
    }

    /// <summary>
    /// The document rebuilt from the log alone, with no snapshot in the way.
    /// </summary>
    /// <remarks>
    /// The uncollected control. It has to bypass <see cref="DocumentStore"/>,
    /// because that starts from the latest snapshot — which is the thing
    /// collection has just changed.
    /// </remarks>
    private static async Task<Replica> ReplayAsync(
        EditorApiFactory factory, Guid documentId, Guid? asReplica = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var rows = await context.DocumentOperations
            .Where(row => row.DocumentId == documentId)
            .OrderBy(row => row.ServerSeq)
            .ToListAsync(TestContext.Current.CancellationToken);

        var replica = new Replica(ReplicaIdConversion.FromGuid(asReplica ?? documentId));
        foreach (var row in rows)
        {
            replica.Apply(OperationMapper.FromRow(row));
        }

        return replica;
    }
}
