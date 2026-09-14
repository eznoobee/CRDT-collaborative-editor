using System.Diagnostics;
using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Domain;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// §10's trace: receive → validate → persist → broadcast, as one related whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vacuity risk, and the whole reason these assert parentage.</b> "The stage
/// spans exist" is satisfied by four unparented activities, which carry exactly
/// the information four log lines carry and are not a trace. The property that
/// makes a trace worth having is that the stages are attributable to <i>one</i>
/// submission, so every assertion below is about a parent id rather than about a
/// name being present.
/// </para><para>
/// <b>And the pair.</b> "All the stages share a root" is satisfied by a single
/// process-wide or connection-wide root, under which every submission the server
/// ever handled is one undifferentiated blob. Two submissions must produce two
/// roots, or the trace cannot answer "which one was slow" — which is the only
/// question §8's p99 target leads to.
/// </para><para>
/// <b>§12 Q2 — does anything invoke this, or only the test?</b> The spans are
/// started by the hub on the ordinary submit path, so every other test in this
/// suite runs them; the listener here is the only thing this file adds. That
/// also means a span left undisposed would surface as a wrong parent in these
/// assertions rather than as a leak nobody sees.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class EditorTracingTests
{
    private readonly EditorFixture _fixture;

    public EditorTracingTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_four_stages_hang_off_one_submission()
    {
        _fixture.RequireBoth();
        using var trace = new ActivityCollector(EditorTracing.SourceName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "trace-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "trace-writer", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "trace-writer", documentId);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("hello"))).Code);

        var submit = trace.Only(EditorTracing.Submit);

        // Parentage, not presence. Four unrelated spans would satisfy a
        // presence check and localise nothing.
        foreach (var stage in new[]
        {
            EditorTracing.Validate, EditorTracing.Persist, EditorTracing.Broadcast,
        })
        {
            Assert.Equal(submit.SpanId, trace.Only(stage).ParentSpanId);
        }

        // Siblings in the order §10 names, rather than a chain: a stage left
        // undisposed becomes the parent of the next one, which reports
        // persistence as happening inside validation.
        Assert.Equal(
            [EditorTracing.Validate, EditorTracing.Persist, EditorTracing.Broadcast],
            trace.Finished
                .Where(activity => activity.ParentSpanId == submit.SpanId)
                .Select(activity => activity.OperationName));

        // And the root outlives its stages, which is what makes the aggregate
        // and the breakdown comparable at all.
        Assert.True(
            submit.Duration >= trace.Only(EditorTracing.Persist).Duration,
            "the submission span was shorter than a stage inside it");
    }

    [Fact]
    public async Task Two_submissions_are_two_traces()
    {
        // The pair. A root started per connection or per process would put
        // every submission the server handled under one span, and "which one
        // was slow" would have no answer.
        _fixture.RequireBoth();
        using var trace = new ActivityCollector(EditorTracing.SourceName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "trace-two-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "trace-two", Role.Editor);

        await using var client = await DocumentClient.JoinAsync(factory, "trace-two", documentId);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("a"))).Code);
        Assert.Null((await client.SubmitAsync(client.Writer.Type("b"))).Code);

        var roots = trace.Named(EditorTracing.Submit);
        Assert.Equal(2, roots.Count);
        Assert.Equal(2, roots.Select(activity => activity.SpanId).Distinct().Count());

        // Each with its own persistence, under its own root: two roots sharing
        // one set of stages would pass the count above.
        var persists = trace.Named(EditorTracing.Persist);
        Assert.Equal(
            [.. roots.Select(activity => activity.SpanId)],
            [.. persists.Select(activity => activity.ParentSpanId)]);
    }

    [Fact]
    public async Task A_refusal_is_traced_as_far_as_it_got()
    {
        // The diagnostic case, and the one a trace started after the §7 checks
        // would lose: a client whose submissions are all being refused produces
        // no successful submission to look at, which is precisely when someone
        // goes looking.
        _fixture.RequireBoth();
        using var trace = new ActivityCollector(EditorTracing.SourceName);
        await using var factory = new EditorApiFactory(_fixture);

        var documentId = await DocumentSetup.DocumentAsync(factory, "trace-reject-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "trace-viewer", Role.Viewer);

        await using var viewer = await DocumentClient.JoinAsync(factory, "trace-viewer", documentId);
        Assert.NotNull((await viewer.SubmitAsync(viewer.Writer.Type("nope"))).Code);

        var submit = trace.Only(EditorTracing.Submit);

        Assert.Equal(ActivityStatusCode.Error, submit.Status);
        Assert.Equal("forbidden", submit.StatusDescription);

        // Stopped at the §7 check, so nothing was written. A trace that
        // reported a persist stage for a refused submission would send someone
        // looking at the database for a row that does not exist.
        Assert.Empty(trace.Named(EditorTracing.Persist));
    }
}
