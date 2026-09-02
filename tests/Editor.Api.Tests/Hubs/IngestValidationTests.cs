using System.Net;
using System.Net.Http.Json;
using System.Text;
using Crdt.Core;
using Editor.Api.Hubs;
using Editor.Domain;
using Editor.Infrastructure.Authorization;
using Editor.Infrastructure.Ingest;
using Editor.Infrastructure.Persistence;
using Editor.Infrastructure.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Editor.Api.Tests.Hubs;

/// <summary>
/// §7's ingest rules, each with a test proving the limit rejects.
/// </summary>
[Collection(nameof(EditorTests))]
public sealed class IngestValidationTests
{
    private readonly EditorFixture _fixture;

    public IngestValidationTests(EditorFixture fixture) => _fixture = fixture;

    private sealed record Negotiated(string Ticket, Guid DocumentId, Guid ReplicaId, Role Role);

    [Fact]
    public async Task An_oversized_message_closes_the_connection()
    {
        // §7's abuse-resistance rule: a malformed or oversized message closes
        // the connection after logging. That is SignalR's receive limit doing
        // it, and the point of this test is that the limit is the configured
        // cap — it defaults to 32 KB, half of §7's, so left alone the
        // configured value would be unreachable and the ingest byte check would
        // be code that never runs.
        await using var session = await SessionAsync("too-big");

        var limits = session.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IngestLimits>>().Value;

        await Assert.ThrowsAnyAsync<Exception>(
            () => session.SubmitAsync(new byte[limits.MaxMessageBytes + 1]));

        Assert.Equal(HubConnectionState.Disconnected, session.Connection.State);
    }

    [Fact]
    public async Task A_message_under_the_configured_cap_is_not_closed_on()
    {
        // Without this, the test above passes against the framework's 32 KB
        // default just as happily as against §7's 64 KB: an oversized message
        // closes the connection either way. This is the half that pins which
        // limit is in force — a payload comfortably over the default and under
        // the configured cap has to reach the validator and come back as a
        // rejection rather than as a closed connection.
        //
        // 40 KB and not 63: SignalR's default JSON protocol base64-encodes a
        // byte[] argument, so a payload costs a third more than its own size on
        // the wire and §7's 64 KB message cap admits roughly 47 KB of
        // operations. That inflation is worth fixing — it is 33% of every
        // keystroke batch — but the fix is a protocol change that the
        // TypeScript client has to make too, so it belongs with the client
        // rather than here.
        await using var session = await SessionAsync("under-cap");

        var result = await session.SubmitAsync(new byte[40 * 1024]);

        Assert.Equal(IngestRejection.Malformed, result.Code);
        Assert.Equal(HubConnectionState.Connected, session.Connection.State);
    }

    [Fact]
    public async Task The_ingest_byte_cap_rejects_a_payload_over_the_limit()
    {
        // The validator's own check, exercised where the transport cannot
        // preempt it. It is the cap on the operation bytes rather than on the
        // hub envelope around them, and it runs before the decode because the
        // decode is what allocates — checking size afterwards would leave the
        // limit protecting nothing it was written to protect.
        _fixture.RequireBoth();
        await using var factory = new EditorApiFactory(_fixture);

        var validator = factory.Services.GetRequiredService<IngestValidator>();
        var limits = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IngestLimits>>().Value;

        var result = await validator.ValidateAsync(
            Guid.CreateVersion7(),
            ReplicaIdConversion.FromGuid(Guid.CreateVersion7()),
            new byte[limits.MaxMessageBytes + 1],
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestRejection.MessageTooLarge, result.Rejection);
    }

    [Fact]
    public async Task A_batch_over_the_operation_cap_is_rejected()
    {
        await using var session = await SessionAsync("too-many-ops");

        var limits = session.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IngestLimits>>().Value;

        var oversized = session.Writer.Type(new string('a', limits.MaxOperationsPerBatch + 1));

        // Still inside the byte cap, so this is the operation count rejecting
        // and not the size check doing its job by accident.
        Assert.InRange(oversized.Length, 0, limits.MaxMessageBytes);

        var result = await session.SubmitAsync(oversized);

        Assert.Equal(IngestRejection.BatchTooLarge, result.Code);
    }

    [Fact]
    public async Task A_batch_at_the_operation_cap_is_accepted()
    {
        // Off-by-one in the safe direction is still a bug: a cap that rejected
        // the 256th operation would silently cost every client a round trip.
        await using var session = await SessionAsync("at-the-cap");

        var limits = session.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<IngestLimits>>().Value;

        var result = await session.SubmitAsync(
            session.Writer.Type(new string('a', limits.MaxOperationsPerBatch)));

        Assert.Null(result.Code);
        Assert.Equal(limits.MaxOperationsPerBatch, result.Accepted);
    }

    [Fact]
    public async Task Bytes_that_are_not_an_operation_batch_are_rejected()
    {
        await using var session = await SessionAsync("malformed");

        var result = await session.SubmitAsync([1, 2, 3, 4]);

        Assert.Equal(IngestRejection.Malformed, result.Code);
    }

    [Fact]
    public async Task An_operation_claiming_another_replica_is_rejected()
    {
        // §7. The batch-level check in the hub compares the message's replica
        // field; this is the per-operation check, and the difference matters:
        // the message can name the right replica while an operation inside it
        // names another.
        await using var session = await SessionAsync("forged-replica");

        var forged = ReplicaWriter.TypeAs(Guid.CreateVersion7(), 0, "a");
        var result = await session.SubmitAsync(forged);

        Assert.Equal(IngestRejection.ReplicaMismatch, result.Code);
    }

    [Fact]
    public async Task An_honest_first_operation_does_not_excuse_a_forged_later_one()
    {
        // The check is per operation, not per batch. Validating only the first
        // would let a client attach one honest operation to a batch of forged
        // ones — and the forged ones would still converge everywhere.
        await using var session = await SessionAsync("forged-tail");

        var honest = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var other = ReplicaIdConversion.FromGuid(Guid.CreateVersion7());

        var first = new ElementId(honest, 0);
        var mixed = OperationBinary.Encode(
        [
            new InsertOperation(first, new Rune('a'), null, Side.Right, null),
            new InsertOperation(new ElementId(other, 0), new Rune('b'), first, Side.Right, null),
        ]);

        var result = await session.SubmitAsync(mixed);

        Assert.Equal(IngestRejection.ReplicaMismatch, result.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task A_sequence_number_that_skips_ahead_is_rejected(int skip)
    {
        // §5 makes density a correctness property of the version vector, not a
        // convention: a gap is a missing operation that every replica would
        // wait for forever.
        await using var session = await SessionAsync($"gap-{skip}");

        var writer = new ReplicaWriter(session.Negotiated.ReplicaId, startSeq: (ulong)skip);
        var result = await session.SubmitAsync(writer.Type("a"));

        Assert.Equal(IngestRejection.SequenceGap, result.Code);
    }

    [Fact]
    public async Task A_replayed_sequence_number_is_rejected()
    {
        // The other direction, and the one the log would swallow: the primary
        // key on (document, replica, seq) makes a replay a no-op insert, so
        // without this check the server would answer "accepted" to an operation
        // it did not store and never broadcast.
        await using var session = await SessionAsync("replay");

        Assert.Null((await session.SubmitAsync(session.Writer.Type("a"))).Code);

        var replay = new ReplicaWriter(session.Negotiated.ReplicaId, startSeq: 0);
        var result = await session.SubmitAsync(replay.Type("a"));

        Assert.Equal(IngestRejection.SequenceGap, result.Code);
    }

    [Fact]
    public async Task A_gap_inside_an_otherwise_dense_batch_is_rejected()
    {
        await using var session = await SessionAsync("inner-gap");

        var replica = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var first = new ElementId(replica, 0);
        var gapped = OperationBinary.Encode(
        [
            new InsertOperation(first, new Rune('a'), null, Side.Right, null),
            new InsertOperation(new ElementId(replica, 2), new Rune('b'), first, Side.Right, null),
        ]);

        var result = await session.SubmitAsync(gapped);

        Assert.Equal(IngestRejection.SequenceGap, result.Code);
    }

    [Fact]
    public async Task A_rejected_batch_writes_none_of_itself()
    {
        // All or nothing. Applying the valid prefix would leave the sequence
        // dense on the server and gapped on the client, and the client's next
        // operation — correctly numbered from its own point of view — would be
        // rejected forever.
        await using var session = await SessionAsync("all-or-nothing");

        var replica = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var first = new ElementId(replica, 0);
        var gapped = OperationBinary.Encode(
        [
            new InsertOperation(first, new Rune('a'), null, Side.Right, null),
            new InsertOperation(new ElementId(replica, 7), new Rune('b'), first, Side.Right, null),
        ]);

        Assert.Equal(IngestRejection.SequenceGap, (await session.SubmitAsync(gapped)).Code);

        await using var scope = session.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var stored = await context.DocumentOperations
            .CountAsync(
                row => row.DocumentId == session.Negotiated.DocumentId,
                TestContext.Current.CancellationToken);

        Assert.Equal(0, stored);

        // And the client can still send the batch it should have sent.
        Assert.Null((await session.SubmitAsync(session.Writer.Type("a"))).Code);
    }

    [Fact]
    public async Task An_accepted_batch_reaches_the_log()
    {
        // Every rejection test above would pass against a hub that wrote
        // nothing at all.
        await using var session = await SessionAsync("accepted");

        Assert.Null((await session.SubmitAsync(session.Writer.Type("hello"))).Code);

        await using var scope = session.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var stored = await context.DocumentOperations
            .Where(row => row.DocumentId == session.Negotiated.DocumentId)
            .OrderBy(row => row.Seq)
            .Select(row => row.Value)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["h", "e", "l", "l", "o"], stored);
    }

    [Fact]
    public async Task The_expected_sequence_survives_losing_the_cache()
    {
        // §7 and §8: the expected next value is reconstructible from Postgres,
        // and the in-memory copy is a cache. An instance that lost it — a
        // failover, a restart — must reject and accept exactly what it did
        // before, or a client's next keystroke fails for a reason no one can
        // see.
        await using var session = await SessionAsync("cold-instance");

        Assert.Null((await session.SubmitAsync(session.Writer.Type("abc"))).Code);

        session.Factory.Services.GetRequiredService<DocumentIngestState>()
            .Forget(session.Negotiated.DocumentId);

        var replay = new ReplicaWriter(session.Negotiated.ReplicaId, startSeq: 0);
        Assert.Equal(IngestRejection.SequenceGap, (await session.SubmitAsync(replay.Type("a"))).Code);
        Assert.Null((await session.SubmitAsync(session.Writer.Type("d"))).Code);
    }

    [Fact]
    public async Task A_document_at_its_size_cap_stops_accepting_text()
    {
        // §7 caps live text per document. Tightened by configuration for the
        // test rather than typing five megabytes, which is what "all
        // configurable" is for — and the configuration path is the one a
        // deployment uses, so exercising it is worth more than a constant.
        await using var session = await SessionAsync("full", limits: new Dictionary<string, string?>
        {
            ["Ingest:MaxDocumentBytes"] = "8192",
        });

        var filled = 0;
        for (var batch = 0; batch < 40; batch++)
        {
            var result = await session.SubmitAsync(session.Writer.Type(new string('a', 256)));
            if (result.Code is not null)
            {
                Assert.Equal(IngestRejection.DocumentFull, result.Code);

                // Rejected at the cap, not well before it: a limit that refused
                // at half its stated size would pass a test that only checked
                // that something was eventually refused.
                Assert.InRange(filled, 8192 - 256, 8192);
                return;
            }

            filled += 256;
        }

        Assert.Fail($"§7 caps live text per document; it grew to {filled} bytes past a cap of 8192.");
    }

    [Fact]
    public async Task A_document_at_its_replica_cap_refuses_another_connection()
    {
        // Refused at negotiate, not at the first keystroke: a client that
        // connected, rendered the document and then could not type would look
        // like a bug in the editor.
        await using var factory = new EditorApiFactory(_fixture, settings: new Dictionary<string, string?>
        {
            ["Ingest:MaxReplicasPerDocument"] = "2",
        });

        _fixture.RequireBoth();

        var owner = await factory.CreateUserAsync("owner-cap", TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        using var client = factory.ClientFor("owner-cap");
        var uri = new Uri($"/documents/{documentId}/negotiate", UriKind.Relative);

        for (var accepted = 0; accepted < 2; accepted++)
        {
            using var allowed = await client.PostAsync(uri, null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await client.PostAsync(uri, null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(
            IngestRejection.TooManyReplicas,
            await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private async Task<Session> SessionAsync(
        string subject, Dictionary<string, string?>? limits = null)
    {
        _fixture.RequireBoth();
        var factory = new EditorApiFactory(_fixture, settings: limits);

        var owner = await factory.CreateUserAsync(
            "owner-" + subject, TestContext.Current.CancellationToken);
        var userId = await factory.CreateUserAsync(subject, TestContext.Current.CancellationToken);
        var documentId = await factory.CreateDocumentAsync(
            owner, cancellationToken: TestContext.Current.CancellationToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDocumentRoleWriter>()
                .SetRoleAsync(documentId, userId, Role.Editor, owner, TestContext.Current.CancellationToken);
        }

        using var client = factory.ClientFor(subject);
        using var response = await client.PostAsync(
            new Uri($"/documents/{documentId}/negotiate", UriKind.Relative),
            null,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var negotiated = await response.Content.ReadFromJsonAsync<Negotiated>(
            TestContext.Current.CancellationToken);

        Assert.NotNull(negotiated);

        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, $"/hub/editor?access_token={Uri.EscapeDataString(negotiated.Ticket)}"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.WebSockets;
                    options.WebSocketFactory = async (context, cancellationToken) =>
                        await factory.Server.CreateWebSocketClient()
                            .ConnectAsync(context.Uri, cancellationToken);
                })
            .Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);

        return new Session(factory, connection, negotiated);
    }

    private sealed class Session : IAsyncDisposable
    {
        internal Session(EditorApiFactory factory, HubConnection connection, Negotiated negotiated)
        {
            Factory = factory;
            Connection = connection;
            Negotiated = negotiated;
            Writer = new ReplicaWriter(negotiated.ReplicaId);
        }

        public EditorApiFactory Factory { get; }

        public HubConnection Connection { get; }

        public Negotiated Negotiated { get; }

        public ReplicaWriter Writer { get; }

        public Task<SubmitResult> SubmitAsync(byte[] operations) =>
            Connection.InvokeAsync<SubmitResult>(
                "SubmitAsync",
                new OperationBatchMessage(Negotiated.DocumentId, Negotiated.ReplicaId, operations),
                TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Factory.DisposeAsync();
        }
    }
}
