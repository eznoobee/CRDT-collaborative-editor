using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// Resuming a replica across a reload (§7).
/// </summary>
/// <remarks>
/// The property this exists for: a client that reloads holds an outbox of
/// operations it authored under its previous replica id, and §7's tier-1 check
/// rejects any batch whose replica id is not this connection's binding. Without
/// resumption that unsent work is unsubmittable, and the alternatives are worse
/// — re-authoring under a new id changes operation identity, so a
/// partially-delivered batch arrives twice under two names and the characters
/// appear twice.
/// <para>
/// The vacuity risks, named before these were written. First: every refusal here
/// results in a <em>fresh</em> replica rather than an error, so a server that
/// ignored the request entirely and always minted a fresh id would pass every
/// negative test on its own. Each negative is therefore paired with the positive
/// that must still work, and the assertions are on <c>Resumed</c> as well as on
/// the id — a flag the server has to set deliberately. Second: a negative can
/// pass for the wrong reason, because five checks guard resumption and any one
/// of them refusing looks identical from outside. So each test disturbs exactly
/// one check and leaves the other four satisfiable.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ReplicaResumptionTests
{
    private readonly EditorFixture _fixture;

    public ReplicaResumptionTests(EditorFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Waits until the previous session's replica claim is actually gone.
    /// </summary>
    /// <remarks>
    /// Disposing a client returns before <c>OnDisconnectedAsync</c> has run, and
    /// that handler is what releases §7's claim. A test that negotiates
    /// immediately after disposing is racing it — and the race is not benign,
    /// because five checks guard resumption and the live-claim check refusing
    /// looks identical from outside to any of the other four refusing. A test
    /// aimed at retirement then passes without retirement being consulted.
    /// <para>
    /// This waited on the connection count first, which is released-then-removed
    /// and so *should* imply the claim is gone. Under sabotage the retirement
    /// test was caught on one run and missed on the next — the proxy correlates
    /// with the property without being it. Waiting on the claim itself is
    /// waiting on exactly what the next negotiate needs, which is the only
    /// version that cannot drift from it.
    /// </para>
    /// </remarks>
    private static async Task ReleasedAsync(
        EditorApiFactory factory, Guid documentId, Guid replicaId)
    {
        var claims = factory.Services.GetRequiredService<IReplicaClaims>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline
            && await claims.IsHeldAsync(documentId, replicaId, TestContext.Current.CancellationToken))
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(
            await claims.IsHeldAsync(documentId, replicaId, TestContext.Current.CancellationToken),
            "the previous session never released its replica claim");
    }

    [Fact]
    public async Task A_reload_continues_its_replica_and_can_submit_the_work_it_already_authored()
    {
        // The whole point, asserted as the thing a user would notice: the
        // operations typed before the reload are accepted after it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-resume");
        await DocumentSetup.GrantAsync(factory, documentId, "resumer", Role.Editor);

        Guid replicaId;
        byte[] unsent;

        await using (var before = await DocumentClient.JoinAsync(factory, "resumer", documentId))
        {
            replicaId = before.Negotiated.ReplicaId;
            Assert.False(before.Negotiated.Resumed);

            Assert.Null((await before.SubmitAsync(before.Writer.Type("sent"))).Code);

            // Authored, never submitted — the outbox a reload has to keep.
            unsent = before.Writer.Type("unsent");
        }

        await ReleasedAsync(factory, documentId, replicaId);

        await using var after = await DocumentClient.JoinAsync(
            factory, "resumer", documentId, resume: replicaId);

        Assert.True(after.Negotiated.Resumed);
        Assert.Equal(replicaId, after.Negotiated.ReplicaId);

        // Tier-1 compares against the binding, and the binding is the resumed
        // replica, so operations authored before the reload are submittable.
        // Without resumption this is `forbidden` and the work is stranded.
        Assert.Null((await after.SubmitAsync(unsent)).Code);

        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var rows = await context.DocumentReplicas
            .Where(row => row.DocumentId == documentId)
            .CountAsync(TestContext.Current.CancellationToken);

        // Resuming creates no replica. If it did, every reload would burn one of
        // §7's per-document slots and a user could lock themselves out by
        // refreshing.
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task A_replica_another_user_owns_is_not_resumable()
    {
        // §7 check 2. The claimant is a member of the document with the same
        // role, so nothing but ownership separates them — and the refusal is a
        // fresh replica rather than a status, because "that replica belongs to
        // someone else" is a fact about another user's session.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-theft");
        await DocumentSetup.GrantAsync(factory, documentId, "rightful", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "claimant", Role.Editor);

        Guid stolen;
        await using (var rightful = await DocumentClient.JoinAsync(factory, "rightful", documentId))
        {
            stolen = rightful.Negotiated.ReplicaId;
            Assert.Null((await rightful.SubmitAsync(rightful.Writer.Type("a"))).Code);
        }

        // Waited for, not merely initiated. The live-claim check would refuse
        // this on its own, so ownership is only under test once the claim is
        // genuinely released.
        await ReleasedAsync(factory, documentId, stolen);

        await using var claimant = await DocumentClient.JoinAsync(
            factory, "claimant", documentId, resume: stolen);

        Assert.False(claimant.Negotiated.Resumed);
        Assert.NotEqual(stolen, claimant.Negotiated.ReplicaId);
    }

    [Fact]
    public async Task A_replica_from_another_document_is_not_resumable()
    {
        // §7 check 3. Same user, same role, replica genuinely theirs — only the
        // document differs. Without this check a user could carry one replica id
        // between their own documents, and §5's per-replica density rule is
        // stated per document.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var first = await DocumentSetup.DocumentAsync(factory, "owner-doc-one");
        var second = await DocumentSetup.DocumentAsync(factory, "owner-doc-two");
        await DocumentSetup.GrantAsync(factory, first, "traveller", Role.Editor);
        await DocumentSetup.GrantAsync(factory, second, "traveller", Role.Editor);

        Guid elsewhere;
        await using (var onFirst = await DocumentClient.JoinAsync(factory, "traveller", first))
        {
            elsewhere = onFirst.Negotiated.ReplicaId;
            Assert.Null((await onFirst.SubmitAsync(onFirst.Writer.Type("a"))).Code);
        }

        await ReleasedAsync(factory, first, elsewhere);

        await using var onSecond = await DocumentClient.JoinAsync(
            factory, "traveller", second, resume: elsewhere);

        Assert.False(onSecond.Negotiated.Resumed);
        Assert.NotEqual(elsewhere, onSecond.Negotiated.ReplicaId);
    }

    [Fact]
    public async Task A_retired_replica_is_not_resumable()
    {
        // §7 check 4, which follows from §5: a retired replica's operations may
        // already have been collected, so continuing to author under that id
        // would reference elements the GC has forgotten. The fresh id the client
        // gets back is how §5's "resync and discard local state" instruction
        // reaches it.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-retired");
        await DocumentSetup.GrantAsync(factory, documentId, "retiree", Role.Editor);

        Guid retired;
        await using (var before = await DocumentClient.JoinAsync(factory, "retiree", documentId))
        {
            retired = before.Negotiated.ReplicaId;

            // Submits before leaving, which is both realistic — a replica worth
            // resuming is one that was typed into — and necessary: a connection
            // that never sends anything is not observed closing until the host
            // is torn down, so the claim would still be held when this test
            // asks. That is a transport artefact rather than a product defect,
            // but a test built on it is a test that waits for nothing.
            Assert.Null((await before.SubmitAsync(before.Writer.Type("a"))).Code);
        }

        await ReleasedAsync(factory, documentId, retired);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            // Set behind the client's back, because nothing sets it yet (§5,
            // owned by Phase 7). The check has to work before the job exists,
            // or the job will be written against an untested branch.
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
            await context.DocumentReplicas
                .Where(row => row.DocumentId == documentId && row.ReplicaId == retired)
                .ExecuteUpdateAsync(
                    update => update.SetProperty(row => row.RetiredAt, DateTimeOffset.UtcNow),
                    TestContext.Current.CancellationToken);
        }

        await using var after = await DocumentClient.JoinAsync(
            factory, "retiree", documentId, resume: retired);

        Assert.False(after.Negotiated.Resumed);
        Assert.NotEqual(retired, after.Negotiated.ReplicaId);
    }

    [Fact]
    public async Task A_replica_with_a_live_connection_is_not_resumable_and_becomes_so_when_it_closes()
    {
        // §7 check 5, and the pair that makes it mean something. Two live
        // authors under one replica id can mint two different operations with
        // the same ElementId, and peers converge on whichever they saw first —
        // divergence neither peer can detect.
        //
        // The second half is what stops "never resume" passing: once the first
        // connection closes and releases its claim, the same request succeeds.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-two-tabs");
        await DocumentSetup.GrantAsync(factory, documentId, "two-tabs", Role.Editor);

        var held = await DocumentClient.JoinAsync(factory, "two-tabs", documentId);
        var replicaId = held.Negotiated.ReplicaId;

        // Still connected: proven by it still being able to submit, not assumed
        // from the fact that nothing closed it.
        Assert.Null((await held.SubmitAsync(held.Writer.Type("live"))).Code);

        await using (var second = await DocumentClient.JoinAsync(
            factory, "two-tabs", documentId, resume: replicaId))
        {
            Assert.False(second.Negotiated.Resumed);
            Assert.NotEqual(replicaId, second.Negotiated.ReplicaId);
        }

        await held.DisposeAsync();
        await ReleasedAsync(factory, documentId, replicaId);

        await using var afterClose = await DocumentClient.JoinAsync(
            factory, "two-tabs", documentId, resume: replicaId);

        Assert.True(afterClose.Negotiated.Resumed);
        Assert.Equal(replicaId, afterClose.Negotiated.ReplicaId);
    }

    [Fact]
    public async Task Asking_for_a_replica_that_never_existed_gets_a_working_session()
    {
        // §13.13. A client whose stored replica is gone needs a session it can
        // use, not a status it cannot act on — and the response has to say the
        // id is not the one asked for, because that is the client's signal to
        // discard local state (§9).
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-ghost");
        await DocumentSetup.GrantAsync(factory, documentId, "ghost", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(
            factory, "ghost", documentId, resume: Guid.CreateVersion7());

        Assert.False(client.Negotiated.Resumed);
        Assert.NotEqual(Guid.Empty, client.Negotiated.ReplicaId);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("fresh"))).Code);
    }
}
