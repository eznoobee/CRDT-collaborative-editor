using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Tests.Persistence;

/// <summary>Batched persistence (PROJECT_SPEC.md §8).</summary>
[Collection(nameof(PostgresTests))]
public sealed class OperationLogBatcherTests(PostgresFixture fixture)
{
    private static ReplicaId ReplicaIdOf(int n)
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

    private async Task<int> CountAsync(Guid documentId)
    {
        await using var context = fixture.CreateContext();
        return await context.DocumentOperations
            .CountAsync(o => o.DocumentId == documentId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Coalesces_concurrent_submissions_into_fewer_writes()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        await using var batcher = new OperationLogBatcher(
            new OperationLogWriter(fixture.DataSource), BatchingPolicy.Default);

        // Twenty editors each submitting one character is the case §8 is about.
        var submissions = Enumerable.Range(1, 20)
            .Select(n => batcher.SubmitAsync(documentId, Type(ReplicaIdOf(n), "a")))
            .ToArray();

        await Task.WhenAll(submissions);

        Assert.Equal(20, await CountAsync(documentId));

        // The point of batching: far fewer transactions than submissions, each
        // taking the document's advisory lock once instead of twenty times.
        Assert.True(
            batcher.Flushes < 20,
            $"expected coalescing, but {batcher.Flushes} writes served 20 submissions");
    }

    [Fact]
    public async Task Flushes_early_once_the_operation_cap_is_reached()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        await using var batcher = new OperationLogBatcher(
            new OperationLogWriter(fixture.DataSource),
            new BatchingPolicy(TimeSpan.FromSeconds(30), MaxOperations: 4));

        // The window is half a minute, so anything that completes promptly did
        // so because the cap fired, not because time passed.
        var submissions = Enumerable.Range(1, 4)
            .Select(n => batcher.SubmitAsync(documentId, Type(ReplicaIdOf(n), "ab")))
            .ToArray();

        await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(8, await CountAsync(documentId));
    }

    [Fact]
    public async Task A_single_submission_still_completes_within_the_window()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        await using var batcher = new OperationLogBatcher(
            new OperationLogWriter(fixture.DataSource), BatchingPolicy.Default);

        var result = await batcher
            .SubmitAsync(documentId, Type(ReplicaIdOf(1), "solo"))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Written);
        Assert.Equal(4, await CountAsync(documentId));
    }

    [Fact]
    public async Task Documents_do_not_contend_with_each_other()
    {
        fixture.RequireDatabase();

        await using var batcher = new OperationLogBatcher(
            new OperationLogWriter(fixture.DataSource), BatchingPolicy.Default);

        var documents = Enumerable.Range(0, 5).Select(_ => PostgresFixture.NewDocumentId()).ToArray();
        await Task.WhenAll(documents.Select(d => batcher.SubmitAsync(d, Type(ReplicaIdOf(1), "xyz"))));

        foreach (var documentId in documents)
        {
            Assert.Equal(3, await CountAsync(documentId));
        }
    }
}
