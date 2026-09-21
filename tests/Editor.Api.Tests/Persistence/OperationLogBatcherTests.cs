using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

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

    /// <summary>
    /// Records how many writes are in flight per document, and the peak.
    /// </summary>
    /// <remarks>
    /// The probe sits at <see cref="OperationLogWriter"/> rather than inside the
    /// batcher on purpose. The claim is about what happens between entering and
    /// leaving a write, and a batcher reporting its own concurrency would be the
    /// checked party supplying the answer (§13.42).
    /// </remarks>
    private sealed class ConcurrencyProbe(NpgsqlDataSource dataSource)
        : OperationLogWriter(dataSource)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<Guid, int> _inFlight = [];

        /// <summary>The most writes ever in flight for any single document.</summary>
        public int PeakPerDocument { get; private set; }

        /// <summary>The most writes ever in flight across all documents.</summary>
        public int PeakOverall { get; private set; }

        public override async Task<AppendResult> AppendAsync(
            Guid documentId,
            IReadOnlyList<Operation> operations,
            CancellationToken cancellationToken = default)
        {
            Enter(documentId);
            try
            {
                return await base.AppendAsync(documentId, operations, cancellationToken);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight[documentId]--;
                }
            }
        }

        private void Enter(Guid documentId)
        {
            lock (_gate)
            {
                var mine = _inFlight.TryGetValue(documentId, out var current) ? current + 1 : 1;
                _inFlight[documentId] = mine;

                if (mine > PeakPerDocument)
                {
                    PeakPerDocument = mine;
                }

                var total = _inFlight.Values.Sum();
                if (total > PeakOverall)
                {
                    PeakOverall = total;
                }
            }
        }
    }

    [Fact]
    public async Task Never_writes_a_document_twice_at_once_while_writing_documents_in_parallel()
    {
        // §8's adaptive flushing rests on "no write in flight", and this is the
        // sentence that makes that true: one consumer loop per document, each
        // awaiting its write before looking for more work. `server_seq` is
        // assigned by reading the high-water mark under the advisory lock, so
        // two overlapping writes for one document would be individually correct
        // and would still let a client hear about sequence 12 before sequence 11
        // existed — the lock orders the transactions, not their completions.
        //
        // BOTH HALVES, because either alone passes on a broken implementation.
        // A globally serialised writer has a per-document peak of 1 and is a
        // performance defect §8 exists to avoid; a shared queue has documents
        // overlapping and breaks the invariant above. Only the pair says the
        // serialisation is per document.
        fixture.RequireDatabase();

        var documents = Enumerable.Range(0, 4)
            .Select(_ => PostgresFixture.NewDocumentId())
            .ToArray();

        var probe = new ConcurrencyProbe(fixture.DataSource);
        await using var batcher = new OperationLogBatcher(probe, BatchingPolicy.Default);

        // Submitted from many threads, so nothing about the ordering here is
        // doing the work. Each document gets a long enough stream that its
        // queue is non-empty when a write returns, which is the state adaptive
        // flushing is defined by.
        var submissions = new List<Task<AppendResult>>();
        foreach (var documentId in documents)
        {
            for (var n = 1; n <= 12; n++)
            {
                var replica = ReplicaIdOf(n);
                submissions.Add(Task.Run(
                    () => batcher.SubmitAsync(documentId, Type(replica, "a")),
                    TestContext.Current.CancellationToken));
            }
        }

        await Task.WhenAll(submissions);

        Assert.Equal(1, probe.PeakPerDocument);

        Assert.True(
            probe.PeakOverall > 1,
            "no two documents were ever written at once, so this would pass on a "
            + "globally serialised writer — which is the cost §8's per-document "
            + $"queues exist to avoid (peak overall was {probe.PeakOverall})");

        foreach (var documentId in documents)
        {
            Assert.Equal(12, await CountAsync(documentId));
        }
    }

    [Fact]
    public async Task Writes_a_lone_submission_without_waiting_for_company()
    {
        // The adaptive half that the fixed window cannot do. With §8's original
        // 50 ms window this submission waits out the whole window for
        // stragglers that never come, and that wait sits inside the segment
        // §8's 25 ms target measures (7b.4). With no contention there is
        // nothing to amortise, so there is nothing to wait for.
        //
        // Asserted against the batcher's own clock rather than a stopwatch: a
        // wall-clock assertion on a shared CI runner is a flake, and the
        // question here is whether the code waits, not how fast the machine is.
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var clock = new FakeTimeProvider();
        await using var batcher = new OperationLogBatcher(
            new OperationLogWriter(fixture.DataSource), BatchingPolicy.Default, clock);

        // The clock never advances. Under a fixed window this cannot complete:
        // the consumer is waiting on a timer that will not fire.
        await batcher.SubmitAsync(documentId, Type(ReplicaIdOf(1), "a"))
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountAsync(documentId));
        Assert.Equal(1, batcher.Flushes);
    }
}
