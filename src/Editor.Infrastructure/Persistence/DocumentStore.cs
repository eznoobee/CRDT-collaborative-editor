using Crdt.Core;
using Editor.Infrastructure.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Infrastructure.Persistence;

/// <summary>How often to snapshot (PROJECT_SPEC.md §6).</summary>
/// <param name="OperationsPerSnapshot">
/// Operations between snapshots. Zero disables snapshotting entirely.
/// </param>
/// <remarks>
/// There is deliberately no default on the parameter. A defaulted primary
/// constructor parameter on a record struct is bypassed by <c>new()</c> and by
/// <c>default</c>, both of which zero-initialise — so a "default of 500" would
/// silently become "never snapshot" at exactly the call sites that did not think
/// about it. Use <see cref="Default"/>.
/// </remarks>
public readonly record struct SnapshotPolicy(int OperationsPerSnapshot)
{
    /// <summary>The §6 default: every 500 operations.</summary>
    public static SnapshotPolicy Default => new(500);

    /// <summary>
    /// Whether a snapshot is due, given the sequence before and after a batch.
    /// </summary>
    /// <remarks>
    /// Crossing a multiple rather than reaching one exactly: batches move the
    /// sequence by more than one, so an equality test would step over the
    /// threshold and never fire.
    /// </remarks>
    public bool IsDue(long previousServerSeq, long currentServerSeq) =>
        OperationsPerSnapshot > 0
        && previousServerSeq / OperationsPerSnapshot != currentServerSeq / OperationsPerSnapshot;
}

/// <summary>Loads and snapshots documents (PROJECT_SPEC.md §6).</summary>
public sealed class DocumentStore(NpgsqlDataSource dataSource)
{
    private const string LatestSnapshot = """
        SELECT server_seq, state FROM document_snapshots
        WHERE document_id = $1
        ORDER BY server_seq DESC
        LIMIT 1;
        """;

    private const string OperationsAfter = """
        SELECT replica_id, seq, op_type, parent_replica, parent_seq, side,
               right_origin_replica, right_origin_seq, right_origin_is_end,
               value, target_replica, target_seq, server_seq
        FROM document_ops
        WHERE document_id = $1 AND server_seq > $2
        ORDER BY server_seq;
        """;

    private const string InsertSnapshot = """
        INSERT INTO document_snapshots (document_id, server_seq, state, version_vector, created_at)
        VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT (document_id, server_seq) DO NOTHING;
        """;

    /// <summary>
    /// Rebuilds a document: the latest snapshot, then every operation after it.
    /// </summary>
    public async Task<Replica> LoadAsync(
        Guid documentId, ReplicaId asReplica, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var (fromServerSeq, replica) = await ReadSnapshotAsync(
            connection, documentId, asReplica, cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(OperationsAfter, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = fromServerSeq, NpgsqlDbType = NpgsqlDbType.Bigint });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            replica.Apply(OperationMapper.FromRow(ReadRow(reader, documentId)));
        }

        return replica;
    }

    /// <summary>Writes a snapshot at <paramref name="serverSeq"/>.</summary>
    public async Task SaveSnapshotAsync(
        Guid documentId,
        Replica replica,
        long serverSeq,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replica);

        var versionVector = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, count) in replica.VersionVector)
        {
            versionVector[ReplicaIdConversion.ToGuid(id).ToString()] = NormalisedJson.Number(count);
        }

        var builder = new System.Text.StringBuilder();
        builder.Append("{\n");
        NormalisedJson.AppendMap(builder, 1, "versionVector", versionVector);
        builder.Append('\n').Append("}\n");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(InsertSnapshot, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = serverSeq, NpgsqlDbType = NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = SnapshotSerializer.Serialize(replica),
            NpgsqlDbType = NpgsqlDbType.Text,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = builder.ToString(),
            NpgsqlDbType = NpgsqlDbType.Text,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = DateTimeOffset.UtcNow,
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long FromServerSeq, Replica Replica)> ReadSnapshotAsync(
        NpgsqlConnection connection,
        Guid documentId,
        ReplicaId asReplica,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(LatestSnapshot, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0L, new Replica(asReplica));
        }

        var serverSeq = reader.GetInt64(0);
        return (serverSeq, SnapshotSerializer.Deserialize(asReplica, reader.GetString(1)));
    }

    private static DocumentOperationRow ReadRow(NpgsqlDataReader reader, Guid documentId) => new()
    {
        DocumentId = documentId,
        ReplicaId = reader.GetGuid(0),
        Seq = reader.GetInt64(1),
        OpType = reader.GetString(2),
        ParentReplica = reader.IsDBNull(3) ? null : reader.GetGuid(3),
        ParentSeq = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        Side = reader.IsDBNull(5) ? null : reader.GetString(5),
        RightOriginReplica = reader.IsDBNull(6) ? null : reader.GetGuid(6),
        RightOriginSeq = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        RightOriginIsEnd = reader.GetBoolean(8),
        Value = reader.IsDBNull(9) ? null : reader.GetString(9),
        TargetReplica = reader.IsDBNull(10) ? null : reader.GetGuid(10),
        TargetSeq = reader.IsDBNull(11) ? null : reader.GetInt64(11),
        ServerSeq = reader.GetInt64(12),
    };
}
