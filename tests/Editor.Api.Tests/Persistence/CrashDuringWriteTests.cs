using System.Text;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Api.Tests.Persistence;

/// <summary>
/// The crash-during-write requirement (PROJECT_SPEC.md §11, Phase 2 done-when).
/// </summary>
/// <remarks>
/// The backend is terminated mid-transaction with <c>pg_terminate_backend</c>
/// rather than the transaction being rolled back politely. A rollback tests that
/// Postgres honours <c>ROLLBACK</c>; killing the process tests what actually
/// happens when a writer dies holding an advisory lock halfway through a batch,
/// which is the failure the requirement is about.
/// </remarks>
[Collection(nameof(PostgresTests))]
public sealed class CrashDuringWriteTests(PostgresFixture fixture)
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

    private async Task<int> CountAsync(Guid documentId)
    {
        await using var context = fixture.CreateContext();
        return await context.DocumentOperations
            .CountAsync(o => o.DocumentId == documentId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_writer_killed_mid_batch_leaves_no_partial_state()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var cancellationToken = TestContext.Current.CancellationToken;

        int pid;
        await using (var victim = await fixture.DataSource.OpenConnectionAsync(cancellationToken))
        {
            pid = victim.ProcessID;

            await using var transaction = await victim.BeginTransactionAsync(cancellationToken);

            await using (var take = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0));", victim, transaction))
            {
                take.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
                await take.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO document_ops (document_id, replica_id, seq, op_type, value, side,
                                          right_origin_is_end, server_seq, created_at)
                VALUES ($1, $2, 0, 'insert', 'a', 'R', true, 1, now());
                """, victim, transaction))
            {
                insert.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
                insert.Parameters.Add(new NpgsqlParameter
                {
                    Value = ReplicaIdConversion.ToGuid(Replica(1)),
                    NpgsqlDbType = NpgsqlDbType.Uuid,
                });
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            // Half a batch is now written and uncommitted. Kill the writer.
            await using var killer = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend($1);", killer);
            kill.Parameters.Add(new NpgsqlParameter { Value = pid, NpgsqlDbType = NpgsqlDbType.Integer });
            await kill.ExecuteScalarAsync(cancellationToken);
        }

        // Nothing from the dead transaction is visible.
        Assert.Equal(0, await CountAsync(documentId));
    }

    [Fact]
    public async Task The_document_is_writable_again_after_a_writer_dies_holding_the_lock()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var victim = await fixture.DataSource.OpenConnectionAsync(cancellationToken))
        {
            await using var transaction = await victim.BeginTransactionAsync(cancellationToken);
            await using var take = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0));", victim, transaction);
            take.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
            await take.ExecuteNonQueryAsync(cancellationToken);

            await using var killer = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend($1);", killer);
            kill.Parameters.Add(new NpgsqlParameter { Value = victim.ProcessID, NpgsqlDbType = NpgsqlDbType.Integer });
            await kill.ExecuteScalarAsync(cancellationToken);
        }

        // The lock was transaction-scoped, so it died with the backend. If it had
        // leaked, this append would block until the test timed out.
        var writer = new OperationLogWriter(fixture.DataSource);
        var result = await writer
            .AppendAsync(documentId, Type(Replica(1), "recovered"), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);

        Assert.Equal(9, result.Written);

        // Sequence numbering restarts from the committed high water mark, which
        // is zero: the dead transaction reserved nothing that survived it.
        Assert.Equal(9, result.HighestServerSeq);
    }

    [Fact]
    public async Task A_document_survives_a_crash_between_two_committed_batches()
    {
        fixture.RequireDatabase();

        var documentId = PostgresFixture.NewDocumentId();
        var cancellationToken = TestContext.Current.CancellationToken;
        var writer = new OperationLogWriter(fixture.DataSource);
        var store = new DocumentStore(fixture.DataSource);

        var source = new Replica(Replica(1));
        var first = new List<Operation>();
        foreach (var (rune, at) in "hello".EnumerateRunes().Select((r, i) => (r, i)))
        {
            first.Add(source.Insert(at, rune));
        }

        await writer.AppendAsync(documentId, first, cancellationToken);

        // Crash a would-be second writer before it commits anything.
        await using (var victim = await fixture.DataSource.OpenConnectionAsync(cancellationToken))
        {
            await using var transaction = await victim.BeginTransactionAsync(cancellationToken);
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO document_ops (document_id, replica_id, seq, op_type, value, side,
                                          right_origin_is_end, server_seq, created_at)
                VALUES ($1, $2, 99, 'insert', 'z', 'R', true, 999, now());
                """, victim, transaction);
            insert.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = ReplicaIdConversion.ToGuid(Replica(7)),
                NpgsqlDbType = NpgsqlDbType.Uuid,
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await using var killer = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
            await using var kill = new NpgsqlCommand("SELECT pg_terminate_backend($1);", killer);
            kill.Parameters.Add(new NpgsqlParameter { Value = victim.ProcessID, NpgsqlDbType = NpgsqlDbType.Integer });
            await kill.ExecuteScalarAsync(cancellationToken);
        }

        var second = new List<Operation>();
        foreach (var (rune, at) in " world".EnumerateRunes().Select((r, i) => (r, i + 5)))
        {
            second.Add(source.Insert(at, rune));
        }

        await writer.AppendAsync(documentId, second, cancellationToken);

        var loaded = await store.LoadAsync(documentId, Replica(9), cancellationToken);

        // The crashed write left nothing behind: no stray 'z', no gap that
        // swallowed a committed operation.
        Assert.Equal("hello world", loaded.Text);
        Assert.Equal(11, await CountAsync(documentId));
    }
}
