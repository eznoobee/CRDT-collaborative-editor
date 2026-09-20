using System.Text;
using Crdt.Core;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Truncation;

/// <summary>
/// What a prefix truncation would do to reference resolution (row 30).
/// </summary>
/// <remarks>
/// <para>
/// Written before the truncator, to establish what truncation has to account
/// for rather than to discover it afterwards from a red test in production.
/// <b>The claim: removing a prefix of the log makes elements that are still
/// alive indistinguishable from elements that were collected.</b>
/// </para><para>
/// §5's reference resolution asks one question — is this element id in
/// <c>document_ops</c> — and that is sound only while the log is the whole
/// truth. Truncate behind a snapshot and it stops being: an element created
/// before the truncation point lives on in the snapshot, is visible in the
/// document, can legitimately be referenced by any client that holds it, and
/// has no row. The classifier then reads "below the frontier, author known, no
/// operation in the log" and answers <c>resync_required</c> — <b>which tells a
/// client to destroy its unsent work because it referenced a character that is
/// on everybody's screen.</b>
/// </para><para>
/// The truncation here is done by hand, deliberately, and this file is not the
/// end-to-end test row 30 asks for — the breakdown is explicit that a test
/// truncating the log by hand proves the classifier rather than the truncation.
/// This proves something else and narrower: that a truncation which removed a
/// prefix, however it was performed, would produce this. It is the reason the
/// truncator built next removes what collection collected rather than a prefix.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class LogTruncationHazardTests
{
    private readonly EditorFixture _fixture;

    public LogTruncationHazardTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_live_element_whose_row_was_truncated_is_refused_as_collected()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "owner-hazard");
        await DocumentSetup.GrantAsync(factory, documentId, "hazard", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "hazard", documentId);
        var replica = ReplicaIdConversion.FromGuid(client.Negotiated.ReplicaId);

        // Five characters, none of them deleted. Every one of these elements is
        // alive and visible; nothing here is a candidate for collection.
        Assert.Null((await client.SubmitAsync(client.Writer.Type("hello"))).Code);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

            // A frontier that has seen everything, which is what a single
            // acknowledged replica on a quiet document legitimately produces.
            var document = await context.Documents.SingleAsync(
                row => row.Id == documentId, TestContext.Current.CancellationToken);

            document.StabilityFrontier = new Dictionary<Guid, long>
            {
                [client.Negotiated.ReplicaId] = 5,
            };

            // THE TRUNCATION. A prefix, as "truncate the log behind the
            // snapshot" describes it: everything at or below server_seq 3 goes.
            // The elements are untouched in the document; only their rows are
            // gone.
            await context.DocumentOperations
                .Where(row => row.DocumentId == documentId && row.ServerSeq <= 3)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A second client references the first character — an element that is
        // alive, rendered, and legitimately referenceable by anyone holding the
        // document.
        await DocumentSetup.GrantAsync(factory, documentId, "hazard-peer", Role.Editor);
        await using var peer = await DocumentClient.JoinAsync(factory, "hazard-peer", documentId);

        var peerReplica = ReplicaIdConversion.FromGuid(peer.Negotiated.ReplicaId);
        var truncated = new ElementId(replica, 0);

        var referencing = Editor.Infrastructure.Serialization.OperationBinary.Encode(
        [
            new InsertOperation(
                new ElementId(peerReplica, 0), new Rune('!'), truncated, Side.Right, null),
        ]);

        var result = await peer.SubmitAsync(referencing);

        // THE HAZARD, asserted as it is rather than as it should be. This is a
        // characterisation test: it records what a prefix truncation does, so
        // that the reason LogTruncator does not perform one is a fact in the
        // suite rather than a paragraph in a comment.
        //
        // Read it as: a live, visible character was answered with an
        // instruction to destroy unsent work. If this line ever goes red,
        // reference resolution has stopped depending on the log alone — which
        // would be a real improvement, and the truncator's design note has to
        // be revisited rather than this assertion flipped.
        Assert.Equal(IngestRejection.ResyncRequired, result.Code);
    }
}
