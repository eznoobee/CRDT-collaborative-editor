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
    public async Task An_operation_referencing_an_element_the_document_lacks_is_rejected()
    {
        // §5's readiness, enforced rather than buffered. A client can only
        // reference an element it knows about, and it knows about exactly two
        // kinds: its own earlier operations, which density guarantees the
        // server holds, and other replicas' operations, which it learned from a
        // broadcast the server sent only after committing them. So this is a
        // bug or an attack, never a race — and buffering it would be buffering
        // an id that may never arrive, which is the denial of service §5 warns
        // about.
        await using var session = await SessionAsync("unknown-parent");

        var replica = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var absent = new ElementId(ReplicaIdConversion.FromGuid(Guid.CreateVersion7()), 41);

        var orphan = OperationBinary.Encode(
        [
            new InsertOperation(new ElementId(replica, 0), new Rune('a'), absent, Side.Right, null),
        ]);

        Assert.Equal(IngestRejection.UnknownOrigin, (await session.SubmitAsync(orphan)).Code);
    }

    [Fact]
    public async Task A_delete_for_an_element_the_document_lacks_is_rejected()
    {
        await using var session = await SessionAsync("unknown-target");

        var replica = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var absent = new ElementId(ReplicaIdConversion.FromGuid(Guid.CreateVersion7()), 7);

        var orphan = OperationBinary.Encode(
            [new DeleteOperation(new ElementId(replica, 0), absent)]);

        Assert.Equal(IngestRejection.UnknownOrigin, (await session.SubmitAsync(orphan)).Code);
    }

    [Fact]
    public async Task An_operation_may_reference_one_created_earlier_in_the_same_batch()
    {
        // The ordinary case, and the one an over-strict check would break:
        // every batch of typed characters after the first references an element
        // the batch itself creates. Without this, the origin check would reject
        // everything a person types.
        await using var session = await SessionAsync("same-batch");

        Assert.Null((await session.SubmitAsync(session.Writer.Type("hello"))).Code);
    }

    [Fact]
    public async Task An_operation_may_not_reference_one_created_later_in_the_same_batch()
    {
        // Order within the batch matters. A forward reference is not something
        // a client can produce by typing, and accepting it would mean accepting
        // a batch whose elements cannot be placed in the order they arrive.
        await using var session = await SessionAsync("forward-reference");

        var replica = ReplicaIdConversion.FromGuid(session.Negotiated.ReplicaId);
        var later = new ElementId(replica, 1);

        var forward = OperationBinary.Encode(
        [
            new InsertOperation(new ElementId(replica, 0), new Rune('a'), later, Side.Right, null),
            new InsertOperation(later, new Rune('b'), null, Side.Right, null),
        ]);

        Assert.Equal(IngestRejection.UnknownOrigin, (await session.SubmitAsync(forward)).Code);
    }

    [Fact]
    public async Task The_document_size_cap_is_reconstructed_from_postgres()
    {
        // §8: an in-memory per-document cache must be reconstructible and must
        // not be required for correctness after a failover. The size counter is
        // such a cache, and until 3b.4 its Postgres query matched no rows at
        // all — it filtered on op_type 'ins' against a writer storing 'insert'
        // — so a cold instance read zero live bytes and would have accepted
        // writes into a document already at its cap.
        //
        // Nothing went red for a whole phase, because every test filled the
        // document through one instance and the in-memory counter did the work.
        // This test drops the cache first, which is the only way the query is
        // ever the thing under test (§13.16).
        await using var session = await SessionAsync("cold-cap", limits: new Dictionary<string, string?>
        {
            ["Ingest:MaxDocumentBytes"] = "4096",
        });

        var filled = 0;
        while (filled < 4096 - 256)
        {
            Assert.Null((await session.SubmitAsync(session.Writer.Type(new string('a', 256)))).Code);
            filled += 256;
        }

        var state = session.Factory.Services.GetRequiredService<DocumentIngestState>();
        state.Forget(session.Negotiated.DocumentId);

        var reconstructed = await state.LiveBytesAsync(
            session.Negotiated.DocumentId, TestContext.Current.CancellationToken);

        Assert.Equal(filled, reconstructed);

        // And the cap still fires on the cold instance, which is the behaviour
        // the number exists for.
        var results = new List<string?>();
        for (var batch = 0; batch < 4; batch++)
        {
            results.Add((await session.SubmitAsync(session.Writer.Type(new string('a', 256)))).Code);
        }

        Assert.Contains(IngestRejection.DocumentFull, results);
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

    [Fact]
    public async Task A_pasted_run_lands_as_one_row_per_element()
    {
        // §6: the server expands runs into one row per element on ingest, so
        // document_ops keeps one row per element and the primary key keeps
        // deduplication a plain upsert. The client sends one record; Postgres
        // gets five rows.
        await using var session = await SessionAsync("run-ingest");

        var encoded = session.Writer.Type("hello");

        // One record on the wire, five operations after expansion. Without the
        // first assertion this test would pass just as well against a client
        // that sent five insert records, and would then be testing nothing
        // about runs.
        Assert.Single(RecordTags(encoded));
        Assert.Equal(BinaryFormat.OpRun, RecordTags(encoded)[0]);

        var result = await session.SubmitAsync(encoded);
        Assert.Null(result.Code);
        Assert.Equal(5, result.Accepted);

        await using var scope = session.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();
        var rows = await context.DocumentOperations
            .Where(row => row.DocumentId == session.Negotiated.DocumentId)
            .OrderBy(row => row.Seq)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["h", "e", "l", "l", "o"], rows.Select(row => row.Value));

        // Chained, not siblings. Every element after the first names the one
        // before it as its parent — §6 is explicit that assigning them all the
        // same parent would reintroduce the interleaving invariant 8 forbids.
        for (var i = 1; i < rows.Count; i++)
        {
            Assert.Equal(rows[i - 1].ReplicaId, rows[i].ParentReplica);
            Assert.Equal(rows[i - 1].Seq, rows[i].ParentSeq);
            Assert.Equal("R", rows[i].Side);
        }
    }

    [Fact]
    public async Task A_run_past_the_cap_is_refused_with_its_own_code()
    {
        // Distinct from malformed, because the fix is distinct: this client
        // pasted too much at once and needs to split it.
        await using var session = await SessionAsync("run-cap", limits: new Dictionary<string, string?>
        {
            ["Ingest:MaxRunCodePoints"] = "16",
        });

        var result = await session.SubmitAsync(session.Writer.Type(new string('a', 32)));

        Assert.Equal(IngestRejection.RunTooLong, result.Code);
    }

    /// <summary>The record tags in an encoded batch, without decoding it.</summary>
    private static List<byte> RecordTags(byte[] encoded)
    {
        // Header, then the replica table, then the record count: walked by hand
        // so the assertion is about the bytes rather than about what the
        // decoder chose to tell us.
        var offset = 6;
        var (tableLength, read) = Varint(encoded, offset);
        offset += read + ((int)tableLength * 16);

        var (records, countRead) = Varint(encoded, offset);
        offset += countRead;

        // Only the first tag is needed, and finding the rest means decoding.
        return records == 0 ? [] : [encoded[offset]];
    }

    private static (ulong Value, int Read) Varint(byte[] bytes, int offset)
    {
        ulong value = 0;
        var shift = 0;
        var read = 0;

        while (true)
        {
            var b = bytes[offset + read++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0)
            {
                return (value, read);
            }

            shift += 7;
        }
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
            .AddMessagePackProtocol()
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
