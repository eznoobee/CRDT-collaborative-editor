using Crdt.Core;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §9's <c>resync_required</c>, emitted server-side.
/// </summary>
/// <remarks>
/// <para>
/// <b>The reachability problem, stated first because it shapes every test
/// here.</b> §5's collection shrinks the snapshot and never the operation log
/// (7.3), and truncating the log behind a collected snapshot is deferred to 7b.
/// The frontier is clamped to what the log holds. Together those mean an id
/// below the frontier, from an author the frontier knows, is <i>always</i> in
/// the log — so the condition <c>resync_required</c> detects cannot arise from
/// any sequence of client actions today.
/// </para><para>
/// So the classifier tests below construct the frontier directly, and say so
/// rather than being dressed up as end-to-end. What they buy is that the rule is
/// written, narrow, and pinned before the state that triggers it exists; §9
/// specified the client contract first for the same reason. The end-to-end test
/// belongs with log truncation and is carried as a register row, not as a good
/// intention.
/// </para><para>
/// <b>The path that was reachable is tested as closed.</b> Before 7.4 a client
/// alone on a document could push the frontier anywhere by acknowledging a
/// version vector larger than reality — which reached this code, and also made
/// every tombstone in the document collectable. That is a security test, not a
/// route to the classifier.
/// </para><para>
/// <b>Vacuity risk.</b> Every test here could pass over a classifier that
/// answered <c>resync_required</c> for everything, so each of the narrowings has
/// its own case: at or above the frontier, an author the frontier has never
/// heard of, and an id naming a delete rather than an insert. A test that only
/// checked the positive case would be testing that a constant is returned.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class ResyncRequiredTests
{
    private readonly EditorFixture _fixture;

    public ResyncRequiredTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_reference_below_the_frontier_that_the_log_does_not_hold_is_resync_required()
    {
        // The frontier is written directly. Nothing a client can do produces
        // this state while the log is intact — see the remarks.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-resync");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-resync", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "writer-resync", documentId);
        var typed = client.Writer.Type("abc");
        Assert.Null((await client.SubmitAsync(typed)).Code);
        client.ApplyLocal(typed);

        // An author that exists and a sequence number the log never held. The
        // frontier is set above it, which is the state log truncation will
        // produce for real.
        var ghost = Guid.CreateVersion7();
        await FrontierAsync(factory, documentId, new() { [ghost] = 50 });

        var orphan = Insert(client, parent: new ElementId(
            ReplicaIdConversion.FromGuid(ghost), 20));

        Assert.Equal("resync_required", (await client.SubmitAsync(orphan)).Code);
    }

    [Fact]
    public async Task A_reference_at_or_above_the_frontier_is_unknown_origin()
    {
        // The narrowing that stops a still-in-flight operation being answered
        // with an instruction to destroy unsent work. The boundary is exactly
        // one operation wide, so both sides of it are asserted.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-above");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-above", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "writer-above", documentId);
        var typed = client.Writer.Type("abc");
        Assert.Null((await client.SubmitAsync(typed)).Code);
        client.ApplyLocal(typed);

        var ghost = ReplicaIdConversion.FromGuid(Guid.CreateVersion7());
        await FrontierAsync(
            factory, documentId, new() { [ReplicaIdConversion.ToGuid(ghost)] = 20 });

        // Seq 20 with a frontier of 20: the frontier counts operations, so 20
        // is the first one NOT known to be held everywhere.
        Assert.Equal(
            "unknown_origin",
            (await client.SubmitAsync(Insert(client, new ElementId(ghost, 20)))).Code);

        // And one below it is the collected case.
        Assert.Equal(
            "resync_required",
            (await client.SubmitAsync(Insert(client, new ElementId(ghost, 19)))).Code);
    }

    [Fact]
    public async Task A_reference_to_an_author_the_frontier_has_never_heard_of_is_unknown_origin()
    {
        // What stops a new replica's first operation being answered with an
        // instruction to throw its state away.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-unheard-r");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-unheard-r", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "writer-unheard-r", documentId);
        var typed = client.Writer.Type("abc");
        Assert.Null((await client.SubmitAsync(typed)).Code);
        client.ApplyLocal(typed);

        // A frontier that knows somebody, but not this author.
        await FrontierAsync(factory, documentId, new() { [Guid.CreateVersion7()] = 50 });

        var stranger = ReplicaIdConversion.FromGuid(Guid.CreateVersion7());

        Assert.Equal(
            "unknown_origin",
            (await client.SubmitAsync(Insert(client, new ElementId(stranger, 0)))).Code);
    }

    [Fact]
    public async Task A_reference_to_a_delete_is_unknown_origin_and_not_a_collection()
    {
        // Deletes consume sequence numbers, so an id can name an operation the
        // log holds that is not an element. It sits below the frontier and
        // resolves to nothing, so the frontier comparison alone answers
        // resync_required — telling a user to destroy unsent work because of a
        // client bug. The server can tell the two apart and must.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-delref");
        await DocumentSetup.GrantAsync(factory, documentId, "writer-delref", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "writer-delref", documentId);

        var typed = client.Writer.Type("abc");
        Assert.Null((await client.SubmitAsync(typed)).Code);
        client.ApplyLocal(typed);

        // Seq 3 is a delete, not an element.
        var deleted = client.Writer.Delete(2);
        Assert.Null((await client.SubmitAsync(deleted)).Code);
        client.ApplyLocal(deleted);

        var self = client.Negotiated.ReplicaId;
        await FrontierAsync(factory, documentId, new() { [self] = 50 });

        Assert.Equal(
            "unknown_origin",
            (await client.SubmitAsync(
                Insert(client, new ElementId(ReplicaIdConversion.FromGuid(self), 3)))).Code);
    }

    [Fact]
    public async Task A_client_cannot_reach_it_by_claiming_to_hold_more_than_the_log_has()
    {
        // THE SECURITY TEST, and the only path to this code that was ever
        // reachable. A client alone on a document is the whole of the minimum,
        // so before the clamp one acknowledgement claiming a thousand
        // operations against a log of three moved the frontier to 1000 — which
        // made every tombstone in the document collectable, permanently, since
        // the frontier never moves backwards.
        //
        // Asserted as a number rather than as an absence of error: "no
        // resync_required" would also pass if the acknowledgement were ignored
        // entirely.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-liar");
        await DocumentSetup.GrantAsync(factory, documentId, "liar", Role.Editor);

        await using var liar = await DocumentClient.JoinAsync(factory, "liar", documentId);
        var typed = liar.Writer.Type("abc");
        Assert.Null((await liar.SubmitAsync(typed)).Code);
        liar.ApplyLocal(typed);

        await liar.AcknowledgeAsync(new Dictionary<Guid, long>
        {
            [liar.Negotiated.ReplicaId] = 1000,
        });

        await using var scope = factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<IStabilityFrontier>()
            .AdvanceAsync(documentId, TestContext.Current.CancellationToken);

        // Three operations in the log, so three is the most that can be stable.
        Assert.Equal(3, result.Frontier[liar.Negotiated.ReplicaId]);
    }

    /// <summary>An insert naming <paramref name="parent"/>, encoded for submission.</summary>
    private static byte[] Insert(DocumentClient client, ElementId parent) =>
        OperationBinary.Encode(
        [
            new InsertOperation(
                new ElementId(
                    ReplicaIdConversion.FromGuid(client.Negotiated.ReplicaId),
                    client.Writer.NextSeq),
                new System.Text.Rune('x'),
                parent,
                Side.Right,
                null),
        ]);

    /// <summary>Writes §5's frontier directly. See the remarks on this class.</summary>
    private static async Task FrontierAsync(
        EditorApiFactory factory, Guid documentId, Dictionary<Guid, long> frontier)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        var document = await context.Documents.SingleAsync(
            row => row.Id == documentId, TestContext.Current.CancellationToken);

        document.StabilityFrontier = frontier;
        context.Entry(document).Property(row => row.StabilityFrontier).IsModified = true;

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
