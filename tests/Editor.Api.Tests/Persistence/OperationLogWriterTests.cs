using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Tests.Persistence;

/// <summary>Appending to the operation log (PROJECT_SPEC.md §6).</summary>
[Collection(nameof(PostgresTests))]
public sealed class OperationLogWriterTests(PostgresFixture fixture)
{
    private static ReplicaId Replica(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = (byte)n;
        return new ReplicaId(bytes);
    }

    private static List<Operation> Type(ReplicaId id, string text)
    {
        var replica = new Replica(id);
        var operations = new List<Operation>();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            operations.Add(replica.Insert(index++, rune));
        }

        return operations;
    }

    private async Task<List<DocumentOperationRow>> ReadAsync(Guid documentId)
    {
        await using var context = fixture.CreateContext();
        return await context.DocumentOperations
            .Where(o => o.DocumentId == documentId)
            .OrderBy(o => o.ServerSeq)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Assigns_consecutive_sequence_numbers_within_a_batch()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);

        var result = await writer.AppendAsync(
            documentId, Type(Replica(1), "hello"), TestContext.Current.CancellationToken);

        Assert.Equal(5, result.Written);
        Assert.Equal(5, result.HighestServerSeq);

        var rows = await ReadAsync(documentId);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], rows.Select(r => r.ServerSeq));
    }

    [Fact]
    public async Task Continues_the_sequence_across_batches()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);

        await writer.AppendAsync(documentId, Type(Replica(1), "ab"), TestContext.Current.CancellationToken);
        var second = await writer.AppendAsync(
            documentId, Type(Replica(2), "cd"), TestContext.Current.CancellationToken);

        Assert.Equal(4, second.HighestServerSeq);
        var rows = await ReadAsync(documentId);
        Assert.Equal([1L, 2L, 3L, 4L], rows.Select(r => r.ServerSeq));
    }

    [Fact]
    public async Task Duplicate_submission_is_a_no_op()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);
        var operations = Type(Replica(1), "abc");

        await writer.AppendAsync(documentId, operations, TestContext.Current.CancellationToken);
        var again = await writer.AppendAsync(documentId, operations, TestContext.Current.CancellationToken);

        // §6: the primary key makes this a no-op at the database rather than in
        // application code. Resubmitting must not throw and must not duplicate.
        Assert.Equal(0, again.Written);
        Assert.Equal(3, (await ReadAsync(documentId)).Count);
    }

    [Fact]
    public async Task Sequence_numbers_stay_monotonic_under_concurrent_writers()
    {
        fixture.RequireDatabase();

        // The property §6 actually requires: a reader must never see 101 before
        // 100. Gaps would be acceptable; overlap and reordering are not.
        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);

        var batches = Enumerable.Range(1, 8)
            .Select(n => writer.AppendAsync(
                documentId, Type(Replica(n), "xyz"), TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(batches);

        var rows = await ReadAsync(documentId);
        Assert.Equal(24, rows.Count);

        var sequences = rows.Select(r => r.ServerSeq).ToArray();
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
        Assert.Equal([.. Enumerable.Range(1, 24).Select(i => (long)i)], sequences);

        // Each replica's own operations keep their relative order.
        foreach (var group in rows.GroupBy(r => r.ReplicaId))
        {
            Assert.Equal([.. group.Select(r => r.Seq).Order()], group.Select(r => r.Seq));
        }
    }

    [Fact]
    public async Task Round_trips_operations_through_the_log()
    {
        fixture.RequireDatabase();

        // The Editor.Infrastructure mapping is a second implementation of the
        // encoding (§6); this checks it against the algorithm that produced it.
        var documentId = PostgresFixture.NewDocumentId();
        var writer = new OperationLogWriter(fixture.DataSource);

        var source = new Replica(Replica(1));
        var operations = new List<Operation>();
        foreach (var (rune, at) in "abcdef".EnumerateRunes().Select((r, i) => (r, i)))
        {
            operations.Add(source.Insert(at, rune));
        }

        operations.Add(source.Delete(2));

        await writer.AppendAsync(documentId, operations, TestContext.Current.CancellationToken);

        var rebuilt = new Replica(Replica(9));
        foreach (var row in await ReadAsync(documentId))
        {
            rebuilt.Apply(OperationMapper.FromRow(row));
        }

        Assert.Equal(source.Text, rebuilt.Text);
        Assert.Equal(0, rebuilt.PendingCount);
    }
}
