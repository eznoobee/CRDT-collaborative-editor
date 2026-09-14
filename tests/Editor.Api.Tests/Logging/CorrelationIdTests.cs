using Editor.Api.Logging;
using Editor.Api.Tests.Hubs;
using Editor.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Editor.Api.Tests.Logging;

/// <summary>
/// §10's correlation id: per connection for the hub, per request for the API.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these tests have to discriminate, and what they cannot.</b> The
/// obvious wrong implementation is a per-<i>request</i> id — but over WebSockets
/// a SignalR connection <i>is</i> a single HTTP request, the upgrade, so a
/// per-request id is stable for the whole connection and the two are genuinely
/// indistinguishable at that layer for this transport. Saying so plainly rather
/// than implying the tests are stronger than they are.
/// </para><para>
/// <b>The error they do discriminate is a per-invocation id</b>, and that is the
/// one a busy implementation actually produces, because generating an id where
/// you need it is easier than threading one from connect. It is invisible in any
/// test that performs a single operation: every line has an id, each id looks
/// fine, and nothing can be gathered by it. It also leaves connect and
/// disconnect — the two records an operator most wants when a client
/// misbehaves — with no id at all. So every test below spans <b>several
/// invocations on one connection</b>.
/// </para><para>
/// <b>Vacuity risk.</b> Asserting "a correlation id is present" passes against
/// an id that changes on every line, because it never compares two. Every
/// assertion here compares the id on one line with the id on another. And the
/// capture has to know which line carried which scope, which is why these use
/// <see cref="CapturingLoggerProvider.Lines"/> rather than the flattened
/// <c>Records</c> the redaction sentinel wants.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class CorrelationIdTests
{
    private readonly EditorFixture _fixture;

    public CorrelationIdTests(EditorFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Every_line_one_connection_causes_carries_the_same_id()
    {
        // THE TEST THIS TASK EXISTS FOR. Several invocations, plus connect and
        // disconnect, on one connection. A per-invocation id gives each of
        // these a different value and passes every single-operation test.
        _fixture.RequireBoth();
        var sink = new CapturingLoggerProvider();
        await using var factory = Capturing(sink);

        var documentId = await DocumentSetup.DocumentAsync(factory, "corr-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "corr-writer", Role.Editor);

        await using (var client = await DocumentClient.JoinAsync(factory, "corr-writer", documentId))
        {
            Assert.Null((await client.CatchUpAsync()).Code);
            Assert.Null((await client.SubmitAsync(client.Writer.Type("one"))).Code);
            await client.AcknowledgeAsync();
        }

        await Settled(sink);

        var ids = Connection(sink);

        // Connect, three invocations, disconnect: the point is that there are
        // several lines, from different invocations, and one id across them.
        Assert.True(
            ids.Count >= 4,
            $"expected several connection-scoped lines, saw {ids.Count}");

        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task Two_connections_get_different_ids()
    {
        // The pair. Without it, "all lines share an id" is satisfied by a
        // constant, which correlates everything with everything.
        _fixture.RequireBoth();
        var sink = new CapturingLoggerProvider();
        await using var factory = Capturing(sink);

        var documentId = await DocumentSetup.DocumentAsync(factory, "corr-two-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "corr-a", Role.Editor);
        await DocumentSetup.GrantAsync(factory, documentId, "corr-b", Role.Editor);

        await using (var first = await DocumentClient.JoinAsync(factory, "corr-a", documentId))
        {
            Assert.Null((await first.CatchUpAsync()).Code);
        }

        await using (var second = await DocumentClient.JoinAsync(factory, "corr-b", documentId))
        {
            Assert.Null((await second.CatchUpAsync()).Code);
        }

        await Settled(sink);

        Assert.Equal(2, Connection(sink).Distinct().Count());
    }

    [Fact]
    public async Task A_hub_rejection_carries_the_connection_that_caused_it()
    {
        // The id has to reach the product's own records, not only the ones the
        // correlation filter writes. A filter that scopes its own lines and
        // nothing else would satisfy the tests above and correlate nothing an
        // operator actually looks for.
        _fixture.RequireBoth();
        var sink = new CapturingLoggerProvider();
        await using var factory = Capturing(sink);

        var documentId = await DocumentSetup.DocumentAsync(factory, "corr-reject-owner");
        await DocumentSetup.GrantAsync(factory, documentId, "corr-viewer", Role.Viewer);

        await using (var viewer = await DocumentClient.JoinAsync(factory, "corr-viewer", documentId))
        {
            // §7: a viewer may not write, and the hub logs the refusal.
            Assert.NotNull((await viewer.SubmitAsync(viewer.Writer.Type("nope"))).Code);
        }

        await Settled(sink);

        var rejection = sink.Lines.FirstOrDefault(
            line => line.Category.StartsWith("Editor.Api.Hubs", StringComparison.Ordinal)
                && !line.Category.Equals(CorrelationHubFilter.Category, StringComparison.Ordinal)
                && line.Scopes.ContainsKey(Correlation.Key));

        Assert.True(
            rejection is not null,
            "no record written by the hub itself carried a correlation id");

        // And it is the same connection the filter's own records name, rather
        // than an id of its own.
        Assert.Contains(rejection!.Scopes[Correlation.Key], Connection(sink));
    }

    [Fact]
    public async Task A_document_api_request_is_correlated_and_is_not_the_hub_connection()
    {
        // A REST call has no connection, so the request is the widest unit
        // there is — and its id must not be confused with a hub connection's.
        _fixture.RequireBoth();
        var sink = new CapturingLoggerProvider();
        await using var factory = Capturing(sink);

        // A real HTTP request. The first version of this test called
        // DocumentSetup.DocumentAsync, which seeds rows through the DbContext
        // and makes no request at all — register row 25's eleven direct writes,
        // met here as a test that exercised nothing it claimed to.
        using var client = factory.ClientFor("corr-rest");
        using var listed = await client.GetAsync(
            new Uri("/documents", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(listed.IsSuccessStatusCode);

        var requestIds = sink.Lines
            .Where(line => line.Scopes.ContainsKey(Correlation.Key))
            .Where(line => !line.Category.StartsWith("Editor.Api.Hubs", StringComparison.Ordinal))
            .Select(line => line.Scopes[Correlation.Key])
            .Distinct()
            .ToList();

        Assert.NotEmpty(requestIds);

        // One request, one id — not an id per log line inside it.
        Assert.Single(requestIds);
    }

    /// <summary>Correlation ids on the lines the connection filter wrote.</summary>
    private static List<string> Connection(CapturingLoggerProvider sink) =>
    [
        .. sink.Lines
            .Where(line => line.Category == CorrelationHubFilter.Category)
            .Where(line => line.Scopes.ContainsKey(Correlation.Key))
            .Select(line => line.Scopes[Correlation.Key]),
    ];

    /// <summary>
    /// Waits for the disconnect record, which is written after the client has
    /// gone and would otherwise race the assertion.
    /// </summary>
    private static async Task Settled(CapturingLoggerProvider sink)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline
            && !sink.Lines.Any(line => line.Message.Contains(
                "Connection closed", StringComparison.Ordinal)))
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private EditorApiFactory Capturing(CapturingLoggerProvider sink) =>
        new(
            _fixture,
            settings: new()
            {
                ["Logging:LogLevel:Default"] = "Debug",
                ["Logging:LogLevel:Editor.Api"] = "Debug",
            },
            configure: services => services.AddSingleton<ILoggerProvider>(sink));
}
