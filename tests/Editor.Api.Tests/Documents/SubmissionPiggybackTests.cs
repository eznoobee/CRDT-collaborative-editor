using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Api.Tests.Infrastructure;
using Editor.Domain;
using Editor.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Documents;

/// <summary>
/// §5's acknowledgement piggybacked on a submission (register row 32).
/// </summary>
/// <remarks>
/// <para>
/// <b>The vacuity risk is the whole difficulty here, and it is a bad one.</b>
/// The change is a field on a message. A field that is added to the wire and
/// never read produces no failure anywhere: the client sends it, MessagePack
/// carries it, the server ignores it, every existing test passes, and the
/// frontier keeps advancing on the timer exactly as before. So no test here
/// asserts the field arrives. Each asserts <i>the frontier moved</i> in a
/// situation where nothing else could have moved it — no timer, no catch-up, no
/// direct call.
/// </para><para>
/// <b>That requires the other paths to be genuinely absent</b>, which is why
/// these tests never call <c>AcknowledgeAsync</c> and never catch up after
/// joining. A test that did either would pass against a server that dropped
/// <c>Known</c> on the floor.
/// </para><para>
/// <b>§12 Q1 — who is the legitimate user that never performs the action?</b>
/// The viewer, for the fifth time. A report keyed on submission covers writers
/// only, which is exactly why §5 specifies three paths and not one, and why row
/// 32 is an optimisation rather than a correctness fix: the timer is what keeps
/// the frontier moving for a reader, and it stays.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class SubmissionPiggybackTests
{
    private readonly EditorFixture _fixture;

    public SubmissionPiggybackTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_submission_alone_moves_the_frontier()
    {
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "piggy-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "piggy-writer", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "piggy-writer", documentId);

        // No catch-up, no acknowledge, no timer. The submission is the only
        // thing that happens.
        Assert.Null((await client.SubmitReportingAsync(client.Writer.Type("abc"))).Code);

        var frontier = await AdvanceAsync(factory, documentId);

        // Next-expected form: three operations authored and held, so the next
        // one this replica expects from itself is the fourth.
        Assert.Equal(3, frontier.Frontier[client.Negotiated.ReplicaId]);
    }

    [Fact]
    public async Task A_submission_that_does_not_report_leaves_the_frontier_where_it_was()
    {
        // THE PAIR, and the one that makes the test above mean anything. §5's
        // frontier is clamped to what the log holds, so after three operations
        // are appended it could read three for reasons that have nothing to do
        // with the piggyback — the log itself. This asserts the clamp is not
        // what moved it: same traffic, same log, no report, and the frontier
        // stays at zero because this replica has never said what it holds.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "piggy-silent-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "piggy-silent", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "piggy-silent", documentId);

        Assert.Null((await client.SubmitAsync(client.Writer.Type("abc"))).Code);

        var frontier = await AdvanceAsync(factory, documentId);

        Assert.Equal(0, frontier.Frontier[client.Negotiated.ReplicaId]);
    }

    [Fact]
    public async Task The_piggyback_is_counted_under_its_own_source()
    {
        // §10's acknowledgement counter splits by what prompted the report, and
        // a third source that arrived untagged would be invisible inside the
        // timer's count — which is the number an operator reads to decide the
        // frontier is healthy.
        _fixture.RequireBoth();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "piggy-metric-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "piggy-metric", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "piggy-metric", documentId);
        Assert.Null((await client.SubmitReportingAsync(client.Writer.Type("ab"))).Code);

        Assert.Equal(1, metrics.Total("editor.acknowledgements", "via", "submit"));
        Assert.Equal(0, metrics.Total("editor.acknowledgements", "via", "timer"));
        Assert.Equal(0, metrics.Total("editor.acknowledgements", "via", "catchup"));
    }

    [Fact]
    public async Task A_client_that_sends_no_vector_still_submits()
    {
        // The field is nullable because it is the third on an existing message
        // and the interop client does not send it. A server that required it
        // would break that client with a rejection whose cause is a field it
        // has never heard of.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "piggy-null-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "piggy-null", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "piggy-null", documentId);

        Assert.Null((await client.SubmitAsync(client.Writer.Type("x"), known: null)).Code);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("y"), known: [])).Code);
    }

    private static async Task<FrontierResult> AdvanceAsync(
        EditorApiFactory factory, Guid documentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStabilityFrontier>()
            .AdvanceAsync(documentId, TestContext.Current.CancellationToken);
    }
}
