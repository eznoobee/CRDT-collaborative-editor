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
/// §6's periodic snapshot, which until 7b.3 nothing in the server took.
/// </summary>
/// <remarks>
/// <para>
/// <b>Register row 28 is a §13.41 instance at the size of a subsystem.</b>
/// <see cref="SnapshotPolicy"/> and <see cref="DocumentStore.SaveSnapshotAsync"/>
/// were specified, implemented, and tested; nothing in the running server called
/// either, so every document was rebuilt by full replay and no test of the
/// policy or the writer could have shown it. That is why the trigger test is
/// first in this file rather than added once the rest pass: every other test
/// here would pass with the hosted service unregistered.
/// </para><para>
/// <b>The vacuity risk in the comparison (§13.42).</b> "The document loads
/// correctly after a snapshot" is satisfied trivially when there is no snapshot,
/// because <see cref="DocumentStore.LoadAsync"/> falls back to a full replay and
/// then both sides are the same replay. So the snapshot's existence and its
/// sequence are asserted <i>before</i> the comparison, and the two parties read
/// different tables: one loads through <c>document_snapshots</c>, the other
/// replays <c>document_ops</c> and never looks at a snapshot at all.
/// </para><para>
/// <b>The threshold is configuration, and both halves are tested.</b> These
/// drive the wiring at a small configured interval, because crossing 500
/// operations through the hub costs several batches against §7's caps and proves
/// nothing the small number does not. What the small number cannot show is that
/// the product's own default reaches the policy — so that is asserted
/// separately, on a factory with nothing configured.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class PeriodicSnapshotTests
{
    private readonly EditorFixture _fixture;

    public PeriodicSnapshotTests(EditorFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Operations between snapshots, for the tests that drive the sweep.</summary>
    private const int Threshold = 8;

    [Fact]
    public async Task The_sweeper_snapshots_on_its_own_timer_without_anyone_calling_it()
    {
        // FIRST, DELIBERATELY — see the remarks. Nothing here calls the
        // snapshotter or the sweeper: a document is left far enough behind, the
        // clock moves, and a snapshot has to appear on its own.
        _fixture.RequireBoth();
        var clock = new FakeTimeProvider(Now);
        await using var factory = Configured(clock);

        var documentId = await BehindAsync(factory, "snap-timer-owner", "snap-timer");

        var sweeper = factory.Services.GetRequiredService<SnapshotSweeper>();
        Assert.Equal(0, sweeper.Sweeps);
        Assert.Equal(0, await SnapshotSeqAsync(factory, documentId));

        // WHAT THIS ASSERTS, AND WHAT IT DELIBERATELY DOES NOT (register row
        // 37). The claim here is §13.41's: the hosted service ticks on its own
        // and writes snapshots that nobody asked for. It is NOT that *this*
        // document is one of them.
        //
        // It used to be. `SnapshotSweeper` ranks laggards globally and sweeps a
        // bounded batch, this database is shared with every other test's
        // documents, and so "my document was swept" is really "no other test
        // left `BatchSize` documents further behind" — a claim about the rest of
        // the suite, asserted by this one. It was held up by widening the batch
        // to 64, which is tuning a control until the red goes away (§13.37), and
        // 7b.7's large documents turned it red once and green on a re-run, which
        // is the signature.
        //
        // The batch override is gone with it: nothing here depends on the batch
        // being wide, so the test runs against the product's own default.
        // Whether a *given* document reaches head is asserted by
        // `A_due_document_is_snapshotted_at_head`, through the scoped entry
        // point, where no ranking is involved.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (sweeper.Written > 0)
            {
                break;
            }

            clock.Advance(TimeSpan.FromMinutes(2));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(sweeper.Sweeps > 0, "the sweeper never ran on its own timer");

        // `Written` counts writes, not documents examined, and the sweep stops
        // at the first document that is not due — so a non-zero count means the
        // timer reached a due document and snapshotted it without being asked.
        // This test contributes such a document itself, so the pool is non-empty
        // by construction whatever else the suite left behind.
        Assert.True(
            sweeper.Written > 0,
            $"the sweeper ran {sweeper.Sweeps} times on its own timer and wrote nothing");
    }

    [Fact]
    public async Task A_due_document_is_snapshotted_at_head()
    {
        // The other half of what the timer test used to claim, with the ranking
        // taken out: one named document, the scoped entry point, no batch and
        // no competition. Nothing about this can be changed by what another test
        // left in the shared database.
        _fixture.RequireBoth();
        await using var factory = Configured();

        var documentId = await BehindAsync(factory, "snap-head-owner", "snap-head");

        Assert.Equal(0, await SnapshotSeqAsync(factory, documentId));

        await using var scope = factory.Services.CreateAsyncScope();
        var snapshotter = scope.ServiceProvider.GetRequiredService<IPeriodicSnapshotter>();

        Assert.True(
            await snapshotter.SnapshotDocumentAsync(documentId, TestContext.Current.CancellationToken),
            "a document past the threshold was not snapshotted");

        // At the head of the log, not somewhere behind it. A snapshot stamped
        // at a sequence its state does not contain drops every operation in
        // between, silently, and only on the next load.
        Assert.Equal(
            await HeadSeqAsync(factory, documentId),
            await SnapshotSeqAsync(factory, documentId));
    }

    [Fact]
    public async Task A_swept_snapshot_loads_the_same_document_as_a_replay_that_ignores_it()
    {
        _fixture.RequireBoth();
        await using var factory = Configured();

        var documentId = await BehindAsync(factory, "snap-same-owner", "snap-same");

        // Through the scoped entry point, not the ranked sweep (register row
        // 37). What this test is about is whether a snapshot loads the document
        // a replay does; which documents a bounded global sweep happens to
        // select is a different claim, made by
        // `The_sweeper_snapshots_on_its_own_timer_without_anyone_calling_it`,
        // and importing it here made this test's result depend on how far
        // behind every other test's documents were.
        await using (var sweepScope = factory.Services.CreateAsyncScope())
        {
            Assert.True(
                await sweepScope.ServiceProvider
                    .GetRequiredService<IPeriodicSnapshotter>()
                    .SnapshotDocumentAsync(documentId, TestContext.Current.CancellationToken),
                "nothing was snapshotted, so this compares two replays");
        }

        // Asserted BEFORE the comparison. Without a snapshot, LoadAsync replays
        // the log and agrees with the replay below for reasons that have
        // nothing to do with snapshots — §13.42's shape, where one party's
        // answer comes from the same place as the other's.
        var snapshotSeq = await SnapshotSeqAsync(factory, documentId);
        Assert.True(snapshotSeq > 0, "no snapshot exists for this document");
        Assert.Equal(await HeadSeqAsync(factory, documentId), snapshotSeq);

        // And a tail on top of it, so the comparison covers snapshot + later
        // operations rather than a snapshot that happens to be the whole
        // document.
        await using (var client = await DocumentClient.JoinAsync(factory, "snap-same", documentId))
        {
            Assert.Null((await client.SubmitAsync(client.Writer.Type("tail"))).Code);
        }

        Assert.True(
            await HeadSeqAsync(factory, documentId) > snapshotSeq,
            "nothing was appended after the snapshot");

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<DocumentStore>();

        // Two parties, two tables. This one reads document_snapshots and
        // applies what follows.
        var throughSnapshot = await store.LoadAsync(
            documentId, ReplicaIdConversion.FromGuid(documentId), TestContext.Current.CancellationToken);

        // This one reads document_ops from zero and never looks at a snapshot.
        var replayed = await ReplayAsync(factory, documentId);

        Assert.Equal(replayed.Text, throughSnapshot.Text);
        Assert.Equal(
            SnapshotSerializer.Serialize(replayed),
            SnapshotSerializer.Serialize(throughSnapshot));
        Assert.Equal(0, throughSnapshot.PendingCount);
    }

    [Fact]
    public async Task A_due_document_is_snapshotted_even_when_the_batch_is_full_of_worse_ones()
    {
        // REGISTER ROW 37, MADE DETERMINISTIC. The row says these tests depended
        // on what other tests happened to leave in the shared database: the
        // sweep ranks laggards globally and takes a bounded batch, so a document
        // was snapshotted only if fewer than `BatchSize` others were further
        // behind. That condition arrived by accident in 7b.7 — red once, green
        // on a re-run — and the fix at the time was to widen the batch.
        //
        // So it is constructed here rather than waited for. Enough documents to
        // fill the product's default batch, each left further behind than the
        // one under test, and the claim still has to hold. Under the old
        // arrangement this is precisely the shape that failed.
        _fixture.RequireBoth();
        await using var factory = Configured();

        var documentId = await BehindAsync(factory, "snap-crowd-owner", "snap-crowd");
        var mine = await HeadSeqAsync(factory, documentId);

        // One more than the default batch, each with a longer log than mine, so
        // every one of them outranks it.
        for (var i = 0; i < 17; i++)
        {
            var other = await DocumentSetup.DocumentAsync(factory, $"snap-crowd-{i}-owner");
            await DocumentSetup.GrantAsync(factory, other, $"snap-crowd-{i}", Role.Editor);

            await using var client = await DocumentClient.JoinAsync(factory, $"snap-crowd-{i}", other);
            Assert.Null((await client.SubmitAsync(
                client.Writer.Type(new string('b', Threshold * 4)))).Code);

            Assert.True(
                await HeadSeqAsync(factory, other) > mine,
                "the competing document is not further behind, so this proves nothing");
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var snapshotter = scope.ServiceProvider.GetRequiredService<IPeriodicSnapshotter>();

        Assert.True(
            await snapshotter.SnapshotDocumentAsync(documentId, TestContext.Current.CancellationToken),
            "a due document went unsnapshotted because other documents were further behind");

        Assert.Equal(
            await HeadSeqAsync(factory, documentId),
            await SnapshotSeqAsync(factory, documentId));
    }

    [Fact]
    public async Task A_document_under_the_threshold_is_not_snapshotted()
    {
        // The pair. Without it, "a snapshot appears" is satisfied by a sweep
        // that snapshots everything it sees, which is a full replay and a write
        // per document per minute — the cost §6's interval exists to bound.
        _fixture.RequireBoth();
        await using var factory = Configured();

        var documentId = await DocumentSetup.DocumentAsync(factory, "snap-under-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "snap-under", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "snap-under", documentId))
        {
            // One below the threshold, not one operation: the boundary is the
            // thing that can be off by one.
            Assert.Null((await client.SubmitAsync(client.Writer.Type(new string('x', Threshold - 1)))).Code);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var snapshotter = scope.ServiceProvider.GetRequiredService<IPeriodicSnapshotter>();

        Assert.False(
            await snapshotter.SnapshotDocumentAsync(documentId, TestContext.Current.CancellationToken),
            "a document one operation short of the threshold was snapshotted");

        Assert.Equal(0, await SnapshotSeqAsync(factory, documentId));
    }

    [Fact]
    public async Task The_product_default_is_five_hundred_operations()
    {
        // What the small configured threshold above cannot show. §6 names 500,
        // SnapshotOptions defaults to 500, and the policy the snapshotter
        // actually resolves has to be the one that carries it — a default that
        // never reaches the policy is register row 28 in miniature.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var policy = factory.Services.GetRequiredService<SnapshotPolicy>();

        Assert.Equal(500, policy.OperationsPerSnapshot);
        Assert.True(policy.IsDue(0, 500));
        Assert.False(policy.IsDue(0, 499));
    }

    private EditorApiFactory Configured(FakeTimeProvider? clock = null) =>
        new(
            _fixture,
            settings: new()
            {
                ["Snapshots:OperationsPerSnapshot"] = Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture),

                // No `BatchSize` override. It used to be widened to 64 so that
                // a ranked sweep over a shared database would reach this test's
                // own document — which is tuning a control until a test stops
                // failing (§13.37's second half), and register row 37 is what it
                // cost. Nothing here depends on the batch width any more, so the
                // product's own default is what runs.
            },
            configure: clock is null ? null : services => services.AddSingleton<TimeProvider>(clock));

    /// <summary>A document whose log has run past the threshold.</summary>
    /// <remarks>
    /// Built through the hub, not by writing rows: the claim is that ordinary
    /// submission traffic produces a snapshot, and a setup that appended to the
    /// log directly would not be testing the path a document actually takes.
    /// </remarks>
    private static async Task<Guid> BehindAsync(
        EditorApiFactory factory, string owner, string typist)
    {
        var documentId = await DocumentSetup.DocumentAsync(factory, owner);
        await DocumentSetup.GrantAsync(factory, documentId, typist, Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, typist, documentId);
        Assert.Null((await client.SubmitAsync(client.Writer.Type(new string('a', Threshold + 4)))).Code);

        return documentId;
    }

    private static async Task<long> SnapshotSeqAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        return await context.DocumentSnapshots
            .Where(snapshot => snapshot.DocumentId == documentId)
            .OrderByDescending(snapshot => snapshot.ServerSeq)
            .Select(snapshot => snapshot.ServerSeq)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> HeadSeqAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        return await context.DocumentOperations
            .Where(row => row.DocumentId == documentId)
            .MaxAsync(row => (long?)row.ServerSeq, TestContext.Current.CancellationToken) ?? 0;
    }

    /// <summary>
    /// A replay of the log alone, which is the party that must not have been
    /// told the answer.
    /// </summary>
    private static async Task<Replica> ReplayAsync(EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var rows = await context.DocumentOperations
            .Where(row => row.DocumentId == documentId)
            .OrderBy(row => row.ServerSeq)
            .ToListAsync(TestContext.Current.CancellationToken);

        var replica = new Replica(ReplicaIdConversion.FromGuid(documentId));
        foreach (var row in rows)
        {
            replica.Apply(OperationMapper.FromRow(row));
        }

        return replica;
    }
}
