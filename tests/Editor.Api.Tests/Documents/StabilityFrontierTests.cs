using Editor.Api.Documents;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §5's causal-stability frontier: the minimum of what live replicas hold.
/// </summary>
/// <remarks>
/// The vacuity risks, named before these were written.
/// <para>
/// <b>First: a frontier that never advances is safe, breaks nothing, and passes
/// every correctness test in this repository.</b> Convergence still holds, no
/// document is wrong, and GC simply reclaims nothing forever. Combined with a
/// retirement job that silently never runs — the other half of the pair 7.1
/// guards — the result is a fully green suite over a subsystem that does not
/// work. So the tests here assert that the frontier <i>moves</i>, as a number,
/// and never merely that it is correct.
/// </para><para>
/// <b>Second: replicas at the same version vector give the same minimum as one
/// replica.</b> A test with two identical clients proves nothing about taking a
/// minimum; the replicas here hold genuinely different prefixes.
/// </para><para>
/// <b>Third, and the one the frontier exists for: a live replica must hold it
/// back and a retired one must not.</b> Without the retired half, the frontier
/// is correct and permanently stuck the moment anyone abandons a tab.
/// </para><para>
/// <b>Fourth, predicted before the code: the minimum over an empty set.</b> A
/// document whose replicas are all retired has nothing to take a minimum over,
/// and every natural implementation answers zero — "nothing is stable" about
/// the one document where everything is, because nobody is left who could be
/// missing it. The failure is invisible: an abandoned document is one nobody
/// looks at, and it is also the one whose tombstones are most worth collecting.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class StabilityFrontierTests
{
    private readonly EditorFixture _fixture;

    public StabilityFrontierTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_frontier_is_the_lowest_prefix_any_live_replica_holds()
    {
        // Two replicas at genuinely different vectors, so the answer is a
        // minimum rather than a copy. Author A has written 10 operations; one
        // reader holds 7 of them and the other holds 4.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-min");
        var author = await ReplicaAsync(factory, documentId, "author-min");
        var ahead = await ReplicaAsync(factory, documentId, "ahead-min");
        var behind = await ReplicaAsync(factory, documentId, "behind-min");

        // The author has to have actually written: the frontier is computed
        // over the authors in the operation log, because an author with no
        // operations has nothing that could be stable. An acknowledgement
        // naming an author who has written nothing is noise.
        await SeedOperationsAsync(factory, documentId, author, 11);

        await AcknowledgeAsync(factory, documentId, author, new() { [author] = 10 });
        await AcknowledgeAsync(factory, documentId, ahead, new() { [author] = 7 });
        await AcknowledgeAsync(factory, documentId, behind, new() { [author] = 4 });

        var result = await AdvanceAsync(factory, documentId);

        Assert.True(result.Advanced, "the frontier did not move");
        Assert.Equal(3, result.Live);
        Assert.Equal(4, result.Frontier[author]);
    }

    [Fact]
    public async Task Retiring_the_replica_that_was_holding_it_back_lets_it_advance()
    {
        // The whole mechanism, in one test: the frontier is stuck at what the
        // laggard holds, the laggard is retired, and the frontier moves. This
        // is why register row 1 blocks this work rather than merely preceding
        // it — without retirement, the number below never changes.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-stuck");
        var author = await ReplicaAsync(factory, documentId, "author-stuck");
        var abandoned = await ReplicaAsync(factory, documentId, "abandoned-stuck");

        await SeedOperationsAsync(factory, documentId, author, 13);

        await AcknowledgeAsync(factory, documentId, author, new() { [author] = 12 });
        await AcknowledgeAsync(factory, documentId, abandoned, new() { [author] = 2 });

        var before = await AdvanceAsync(factory, documentId);
        Assert.Equal(2, before.Frontier[author]);

        await RetireAsync(factory, abandoned);

        var after = await AdvanceAsync(factory, documentId);

        Assert.True(after.Advanced, "retiring the laggard did not move the frontier");
        Assert.Equal(1, after.Live);
        Assert.Equal(12, after.Frontier[author]);
    }

    [Fact]
    public async Task A_document_whose_replicas_are_all_retired_has_everything_stable()
    {
        // THE PREDICTED ONE. A minimum over an empty set has no natural value,
        // and answering zero says "nothing is stable" about the document where
        // everything is: nobody is left who could still be missing an
        // operation. Getting it backwards leaves abandoned documents
        // uncollectable forever, which is invisible because nobody opens them.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-empty");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-empty", Role.Editor);

        // Real operations, so "everything" is a number rather than an empty
        // answer that would also satisfy a naive implementation.
        Guid replicaId;
        await using (var client = await DocumentClient.JoinAsync(factory, "writer-empty", documentId))
        {
            replicaId = client.Negotiated.ReplicaId;
            Assert.Null((await client.SubmitAsync(client.Writer.Type("gone tomorrow"))).Code);
        }

        await RetireAsync(factory, replicaId);

        var result = await AdvanceAsync(factory, documentId);

        Assert.Equal(0, result.Live);
        Assert.True(result.Advanced, "an abandoned document's frontier never moved");

        // "gone tomorrow" is 13 code points, submitted as seq 0..12, so the
        // highest is 12 and everything at or below it is stable.
        Assert.Equal(12, result.Frontier[replicaId]);
    }

    [Fact]
    public async Task A_replica_that_has_never_heard_of_an_author_holds_it_at_zero()
    {
        // The missing-key case, and it decides the whole computation. A replica
        // whose vector omits an author holds NOTHING from that author, so its
        // contribution to the minimum is zero. Skipping the missing key instead
        // reads as "no constraint" and marks that author's entire history
        // stable, which is the direction that collects live data.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-unheard");
        var author = await ReplicaAsync(factory, documentId, "author-unheard");
        var oblivious = await ReplicaAsync(factory, documentId, "oblivious");

        await SeedOperationsAsync(factory, documentId, author, 6);

        await AcknowledgeAsync(factory, documentId, author, new() { [author] = 5 });

        // Acknowledges its own existence and nothing of the author's.
        await AcknowledgeAsync(factory, documentId, oblivious, new() { [oblivious] = 0 });

        var result = await AdvanceAsync(factory, documentId);

        Assert.Equal(0, result.Frontier[author]);
    }

    [Fact]
    public async Task The_frontier_never_moves_backwards()
    {
        // §5: recomputed, not accumulated, and a recomputation that produced a
        // lower value would mean a replica un-observed something. Storing the
        // maximum turns that into a frontier that stalls, which is safe,
        // instead of one that retreats after GC has collected below it, which
        // is unrecoverable.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-monotone");
        var author = await ReplicaAsync(factory, documentId, "author-monotone");

        await SeedOperationsAsync(factory, documentId, author, 10);

        await AcknowledgeAsync(factory, documentId, author, new() { [author] = 9 });
        Assert.Equal(9, (await AdvanceAsync(factory, documentId)).Frontier[author]);

        // A newcomer holding nothing. The computed minimum is zero; the stored
        // frontier must not follow it down.
        var newcomer = await ReplicaAsync(factory, documentId, "newcomer-monotone");
        await AcknowledgeAsync(factory, documentId, newcomer, new() { [newcomer] = 0 });

        var after = await AdvanceAsync(factory, documentId);

        Assert.False(after.Advanced);
        Assert.Equal(9, after.Frontier[author]);
    }

    [Fact]
    public async Task A_viewer_who_only_reads_does_not_hold_the_frontier_still()
    {
        // THE ONE THIS PHASE TURNS ON. A viewer submits nothing and catches up
        // once, so under catch-up-and-submission reporting alone its
        // acknowledgement freezes at the moment it connected — and one person
        // reading holds the frontier still for as long as their tab is open,
        // with nothing red anywhere. §13.32, third occurrence.
        //
        // Nothing here types on the viewer's behalf and nothing calls catch-up
        // twice: the viewer connects, reads, and sends the acknowledgement a
        // real client sends on its timer.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-viewer");
        await DocumentSetup.GrantAsync(factory, documentId, "typist", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "watcher", Role.Viewer);

        await using var typist = await DocumentClient.JoinAsync(factory, "typist", documentId);
        await using var watcher = await DocumentClient.JoinAsync(factory, "watcher", documentId);

        // Both catch up at connect, which is all a viewer would ever do.
        Assert.Null((await typist.CatchUpAsync()).Code);
        Assert.Null((await watcher.CatchUpAsync()).Code);

        var batch = typist.Writer.Type("hello");
        Assert.Null((await typist.SubmitAsync(batch)).Code);
        typist.ApplyLocal(batch);

        var arrived = await watcher.NextAsync();
        watcher.Apply(arrived);

        // The typist's own acknowledgement rides on its submission. The
        // viewer's rides on nothing, and this is the call that fixes it.
        await typist.AcknowledgeAsync();
        await watcher.AcknowledgeAsync();

        var result = await AdvanceAsync(factory, documentId);

        // Five code points, seq 0..4. The frontier reaches 4 only because the
        // viewer said so; without its acknowledgement the minimum is whatever
        // it held at connect, which is nothing.
        Assert.True(result.Advanced, "the frontier did not move");
        Assert.Equal(4, result.Frontier[typist.Negotiated.ReplicaId]);
    }

    private static async Task<FrontierResult> AdvanceAsync(
        EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStabilityFrontier>()
            .AdvanceAsync(documentId, TestContext.Current.CancellationToken);
    }

    private static async Task AcknowledgeAsync(
        EditorApiFactory factory, Guid documentId, Guid replicaId, Dictionary<Guid, long> known)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStabilityFrontier>()
            .AcknowledgeAsync(documentId, replicaId, known, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> ReplicaAsync(
        EditorApiFactory factory, Guid documentId, string subject)
    {
        var userId = await factory.CreateUserAsync(subject, TestContext.Current.CancellationToken);
        var replicaId = Guid.CreateVersion7();

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        context.DocumentReplicas.Add(new DocumentReplica
        {
            DocumentId = documentId,
            ReplicaId = replicaId,
            UserId = userId,
            LastSeenAt = DateTimeOffset.UtcNow,
            OperationCount = 0,
            RetiredAt = null,
        });

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return replicaId;
    }

    private static async Task RetireAsync(EditorApiFactory factory, Guid replicaId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        await context.DocumentReplicas
            .Where(row => row.ReplicaId == replicaId)
            .ExecuteUpdateAsync(
                update => update.SetProperty(row => row.RetiredAt, DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
    }

    /// <summary>Writes operation rows so the document has authors to compute over.</summary>
    private static async Task SeedOperationsAsync(
        EditorApiFactory factory, Guid documentId, Guid replicaId, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        for (var seq = 0; seq < count; seq++)
        {
            context.DocumentOperations.Add(new DocumentOperationRow
            {
                DocumentId = documentId,
                ReplicaId = replicaId,
                Seq = seq,
                ServerSeq = seq + 1,
                OpType = "insert",
                Value = "x",
                Side = "R",
                RightOriginIsEnd = true,
            });
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
