using Crdt.Core;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Infrastructure.Persistence;

/// <summary>The result of appending a batch.</summary>
/// <param name="HighestServerSeq">Highest sequence assigned, or the previous high water mark when nothing was new.</param>
/// <param name="Written">Rows actually inserted; duplicates are not counted.</param>
public readonly record struct AppendResult(long HighestServerSeq, int Written);

/// <summary>
/// Appends operations to the log, assigning <c>server_seq</c> (PROJECT_SPEC.md §6).
/// </summary>
/// <remarks>
/// <para>
/// Uses Npgsql directly rather than EF: this is the hot path, and §3 reserves EF
/// for schema and non-hot-path queries.
/// </para>
/// <para>
/// The requirement on <c>server_seq</c> is <em>monotonic visibility</em>, not
/// gaplessness. A reader must never see 101 before 100; a missing 100 is
/// harmless. That is why a Postgres sequence will not do — sequences are gappy
/// <em>and</em> allow a later value to commit first — and why assignment happens
/// under a per-document advisory lock held for the transaction. Taking the lock
/// once per batch rather than once per operation is what makes it affordable.
/// </para>
/// <para>
/// Every statement is parameterised (§6). The advisory key comes from Postgres's
/// own hash of the document id, so every instance derives the same lock from the
/// same document without depending on .NET and Postgres agreeing about hashing.
/// </para>
/// </remarks>
public sealed class OperationLogWriter(NpgsqlDataSource dataSource)
{
    // Two statements rather than one: Postgres' extended protocol cannot carry
    // multiple commands in a single parameterised statement, and parameters are
    // not optional here (§6 forbids concatenated SQL). Both run inside the same
    // transaction, so the advisory lock still covers the read and the insert.
    private const string TakeDocumentLock =
        "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0));";

    private const string HighWaterMark =
        "SELECT COALESCE(MAX(server_seq), 0) FROM document_ops WHERE document_id = $1;";

    private const string InsertOperation = """
        INSERT INTO document_ops (
            document_id, replica_id, seq, op_type,
            parent_replica, parent_seq, side,
            right_origin_replica, right_origin_seq, right_origin_is_end,
            value, target_replica, target_seq, server_seq, created_at)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
        ON CONFLICT (document_id, replica_id, seq) DO NOTHING;
        """;

    /// <summary>Appends a batch, assigning consecutive sequence numbers.</summary>
    public async Task<AppendResult> AppendAsync(
        Guid documentId,
        IReadOnlyList<Operation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var next = await ReserveAsync(connection, transaction, documentId, cancellationToken)
            .ConfigureAwait(false);

        if (operations.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new AppendResult(next, 0);
        }

        var written = 0;
        var assigned = next;

        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var operation in operations)
        {
            assigned++;
            batch.BatchCommands.Add(Command(OperationMapper.ToRow(documentId, operation), assigned));
        }

        written = await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // The batch commits under the lock, so the sequence numbers it assigned
        // become visible together and in order.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AppendResult(assigned, written);
    }

    private static async Task<long> ReserveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid documentId,
        CancellationToken cancellationToken)
    {
        await using (var take = new NpgsqlCommand(TakeDocumentLock, connection, transaction))
        {
            take.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
            await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var read = new NpgsqlCommand(HighWaterMark, connection, transaction);
        read.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        return (long?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L;
    }

    private static NpgsqlBatchCommand Command(DocumentOperationRow row, long serverSeq)
    {
        var command = new NpgsqlBatchCommand(InsertOperation);
        void Add(object? value, NpgsqlDbType type) =>
            command.Parameters.Add(new NpgsqlParameter
            {
                Value = value ?? DBNull.Value,
                NpgsqlDbType = type,
            });

        Add(row.DocumentId, NpgsqlDbType.Uuid);
        Add(row.ReplicaId, NpgsqlDbType.Uuid);
        Add(row.Seq, NpgsqlDbType.Bigint);
        Add(row.OpType, NpgsqlDbType.Varchar);
        Add(row.ParentReplica, NpgsqlDbType.Uuid);
        Add(row.ParentSeq, NpgsqlDbType.Bigint);
        Add(row.Side, NpgsqlDbType.Varchar);
        Add(row.RightOriginReplica, NpgsqlDbType.Uuid);
        Add(row.RightOriginSeq, NpgsqlDbType.Bigint);
        Add(row.RightOriginIsEnd, NpgsqlDbType.Boolean);
        Add(row.Value, NpgsqlDbType.Varchar);
        Add(row.TargetReplica, NpgsqlDbType.Uuid);
        Add(row.TargetSeq, NpgsqlDbType.Bigint);
        Add(serverSeq, NpgsqlDbType.Bigint);
        Add(row.CreatedAt, NpgsqlDbType.TimestampTz);
        return command;
    }
}
