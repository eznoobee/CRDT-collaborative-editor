using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's readings that are counted from the rows (§13.44).
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because row 8's break was invisible.</b> The acknowledgement
/// counter read identically on an instance that wrote nothing and one that wrote
/// everything, because deleting the write left the increment beside it standing.
/// A reading derived from state cannot decouple from what it reports: there is
/// no statement to delete.
/// </para><para>
/// <b>The test that matters is the one below that deletes nothing.</b> It does
/// not sabotage the writer and check the gauge notices — that would be the same
/// mistake one level up, asserting against a break this file chose. It asserts
/// the gauge tracks the rows, and the rows are what the product writes.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class StateReadingTests
{
    private readonly EditorFixture _fixture;

    public StateReadingTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_replica_that_has_never_reported_counts_as_silent()
    {
        // THE READING ROW 8 NEEDED. A client that connects and never says what
        // it holds pins the frontier at nothing for its document, and until
        // this existed the only sign was a counter saying the acknowledgement
        // had arrived.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var readings = factory.Services.GetRequiredService<StateReadings>();

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-silent-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-silent", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-silent", documentId))
        {
            // Connects, types, and never reports — no acknowledge, no catch-up,
            // no vector on the submission. Exactly what row 8's broken instance
            // looked like from the database's side.
            Assert.Null((await client.SubmitAsync(client.Writer.Type("abc"))).Code);
        }

        await readings.ReadAsync(TestContext.Current.CancellationToken);

        // SCOPED TO THIS DOCUMENT, not a delta on the global gauge. §10 counts
        // silent replicas across every document and filters on `last_seen_at >
        // cutoff`, so a replica leaves that count merely by ageing out of the
        // window — nothing has to happen for the number to move. An exact global
        // delta is therefore unstable by construction whenever the database
        // holds replicas near the boundary, which a load run guarantees. Row 37
        // three times over (§13.62).
        Assert.Equal(1, await SilentInAsync(factory, documentId));

        // And the gauge itself is exercised rather than left unasserted: it
        // counts at least what this document contributes, which no ageing
        // elsewhere can falsify.
        Assert.True(
            readings.SilentReplicas >= 1,
            $"a silent replica exists and the gauge read {readings.SilentReplicas}");
    }

    [Fact]
    public async Task A_replica_that_reports_stops_counting_as_silent()
    {
        // The pair, and the one that stops "silent" being a count of replicas.
        // Without it the gauge could simply be LiveReplicas under another name,
        // which would rise identically and diagnose nothing.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var readings = factory.Services.GetRequiredService<StateReadings>();

        var documentId = await DocumentSetup.DocumentAsync(factory, "state-heard-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "state-heard", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "state-heard", documentId))
        {
            Assert.Null((await client.SubmitReportingAsync(client.Writer.Type("abc"))).Code);
        }

        await readings.ReadAsync(TestContext.Current.CancellationToken);

        // The pair to the test above, and the assertion that stops "silent"
        // being `LiveReplicas` under another name: this document has a live
        // replica and none of them is silent.
        Assert.Equal(0, await SilentInAsync(factory, documentId));
        Assert.Equal(1, await LiveInAsync(factory, documentId));
    }

    /// <summary>
    /// Replicas of one document that §10 would count as silent.
    /// </summary>
    /// <remarks>
    /// The same predicate as <see cref="StateReadings"/>'s, restricted to one
    /// document. Written out rather than reusing the gauge, because a test that
    /// asked the gauge whether the gauge is right would be comparing a number to
    /// itself (§13.42); the claim here is that the gauge's definition holds of
    /// the rows this test caused.
    /// </remarks>
    private static async Task<int> SilentInAsync(EditorApiFactory factory, Guid documentId)
    {
        var replicas = await LiveReplicasAsync(factory, documentId);
        return replicas.Count(replica => replica.Acknowledged.Count == 0);
    }

    private static async Task<int> LiveInAsync(EditorApiFactory factory, Guid documentId) =>
        (await LiveReplicasAsync(factory, documentId)).Count;

    /// <summary>
    /// This document's unretired replicas, read into memory.
    /// </summary>
    /// <remarks>
    /// `Acknowledged` is a jsonb dictionary and "is it empty" does not translate
    /// to SQL through EF, so the rows come back and are counted here. There are
    /// one or two of them: the alternative would be hand-written SQL duplicating
    /// §10's query, which is the copy that drifts.
    /// </remarks>
    private static async Task<List<DocumentReplica>> LiveReplicasAsync(
        EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        return await context.DocumentReplicas
            .Where(replica => replica.DocumentId == documentId && replica.RetiredAt == null)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
