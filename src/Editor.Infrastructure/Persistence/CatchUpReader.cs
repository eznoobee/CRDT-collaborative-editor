using System.ComponentModel.DataAnnotations;
using Crdt.Core;
using Editor.Infrastructure.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Infrastructure.Persistence;

/// <summary>When a reconnecting client gets a snapshot instead of a delta.</summary>
public sealed class CatchUpOptions
{
    public const string Section = "CatchUp";

    /// <summary>
    /// Operations beyond which a snapshot is cheaper than the delta.
    /// </summary>
    /// <remarks>
    /// A client that has been away for one keystroke wants two operations, not
    /// a five-megabyte snapshot. One that has been away for a week wants the
    /// snapshot, because replaying its delta means replaying the week. The
    /// threshold is where those cross, and it is configurable because where
    /// they cross depends on the document.
    /// </remarks>
    [Range(1, 100_000)]
    public int MaxDeltaOperations { get; set; } = 2_000;
}

/// <summary>
/// What a reconnecting client needs to become current.
/// </summary>
/// <param name="Snapshot">
/// §6 binary snapshot, or <see langword="null"/> when a delta was enough.
/// </param>
/// <param name="Operations">
/// §6 binary operation batch: the delta, or the tail after the snapshot.
/// </param>
/// <param name="ServerSeq">The highest <c>server_seq</c> this answer covers.</param>
public readonly record struct CatchUp(byte[]? Snapshot, byte[] Operations, long ServerSeq);

/// <summary>
/// Answers "what have I missed" from a client's version vector (§8).
/// </summary>
/// <remarks>
/// The version vector is the cursor, not <c>server_seq</c>. §8 makes broadcast
/// unordered, so a client can have seen 105 without having seen 100 — a
/// server_seq watermark would silently skip whatever fell in the gap. What a
/// client actually knows is per replica and dense (§5), which is exactly what a
/// version vector expresses.
/// </remarks>
public sealed class CatchUpReader
{
    // Everything the client's vector does not already cover: operations from a
    // replica it has never heard of, and operations past the point it reached
    // on the replicas it knows.
    private const string Delta = """
        SELECT o.replica_id, o.seq, o.op_type, o.parent_replica, o.parent_seq, o.side,
               o.right_origin_replica, o.right_origin_seq, o.right_origin_is_end,
               o.value, o.target_replica, o.target_seq, o.server_seq
        FROM document_ops AS o
        LEFT JOIN unnest($2::uuid[], $3::bigint[]) AS known(replica_id, next_seq)
          ON o.replica_id = known.replica_id
        WHERE o.document_id = $1
          AND (known.next_seq IS NULL OR o.seq >= known.next_seq)
        ORDER BY o.server_seq
        LIMIT $4;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly DocumentStore _store;
    private readonly CatchUpOptions _options;

    public CatchUpReader(NpgsqlDataSource dataSource, DocumentStore store, CatchUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        _dataSource = dataSource;
        _store = store;
        _options = options;
    }

    /// <summary>
    /// What <paramref name="known"/> is missing, as a delta or as a snapshot.
    /// </summary>
    /// <param name="documentId">The document.</param>
    /// <param name="known">Per replica, the next sequence number expected.</param>
    /// <param name="forceSnapshot">
    /// Skips the delta path entirely. §13.14: the floor has to be exercised on
    /// its own, because a fallback that only ever runs behind a working fast
    /// path is a fallback nobody has tested.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<CatchUp> ReadAsync(
        Guid documentId,
        IReadOnlyDictionary<ReplicaId, ulong> known,
        bool forceSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(known);

        if (!forceSnapshot && await DeltaIsSafeAsync(documentId, known, cancellationToken).ConfigureAwait(false))
        {
            var delta = await ReadDeltaAsync(documentId, known, cancellationToken).ConfigureAwait(false);
            if (delta is { } operations)
            {
                return new CatchUp(
                    null,
                    OperationBinary.Encode([.. operations.Select(row => row.Operation)]),
                    operations.Count == 0 ? 0 : operations[^1].ServerSeq);
            }
        }

        // The floor. Loading the document is affordable here in a way §8 forbids
        // on the hot path: this runs once per reconnect, not once per keystroke.
        var replica = await _store.LoadAsync(documentId, ReplicaIdConversion.FromGuid(documentId), cancellationToken)
            .ConfigureAwait(false);

        return new CatchUp(
            SnapshotBinary.Encode(replica.Export(), replica.VersionVector),
            OperationBinary.Encode([]),
            await HighWaterMarkAsync(documentId, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether a delta can answer <paramref name="known"/> at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A truncated log cannot serve a client that is behind its snapshot.</b>
    /// §5 makes a replica's sequence dense and readiness refuses an operation
    /// that skips one, so a removed row is a hole a replay stops at — whatever
    /// the row was, and however carefully it was chosen. Truncation removes only
    /// the rows of elements collection collected, which keeps every *reference*
    /// resolvable, and that is a different property from keeping the sequence
    /// dense. 7b.8 established the first and assumed the second.
    /// </para><para>
    /// What it looked like: a client opening a truncated document for the first
    /// time received every surviving row, applied operations up to the hole, and
    /// buffered the rest — including the deletes — in the pending set forever,
    /// showing text with deleted characters still in it while reporting itself
    /// current. Found by asking what a walk step would observe (row 33), which
    /// is the one client nothing else in the suite models: every other test
    /// catches up a replica that was already nearly current.
    /// </para><para>
    /// The test is the snapshot's own version vector rather than a server_seq,
    /// because the client's cursor is a vector: it is behind if the snapshot
    /// knows of any operation it does not. Only consulted for documents that
    /// have actually been truncated, so a document that has never been through a
    /// sweep keeps §8's behaviour exactly — a client one keystroke behind gets
    /// two operations and not five megabytes.
    /// </para>
    /// </remarks>
    private async Task<bool> DeltaIsSafeAsync(
        Guid documentId,
        IReadOnlyDictionary<ReplicaId, ulong> known,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT d.truncated_through, s.version_vector
            FROM documents AS d
            LEFT JOIN LATERAL (
                SELECT version_vector FROM document_snapshots
                WHERE document_id = d.id
                ORDER BY server_seq DESC
                LIMIT 1
            ) s ON TRUE
            WHERE d.id = $1;
            """);

        command.Parameters.AddWithValue(documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (reader.IsDBNull(0) || reader.GetInt64(0) == 0)
        {
            // Never truncated: every row the log ever held is still there.
            return true;
        }

        if (reader.IsDBNull(1))
        {
            // Truncated with no snapshot to fall back on should be impossible —
            // the truncator refuses a document whose snapshot it cannot read —
            // but if it happened, the delta is the only answer available and
            // refusing it would leave the client with nothing at all.
            return true;
        }

        var covered = reader.GetString(1);
        return CoversSnapshot(known, covered);
    }

    /// <summary>Whether <paramref name="known"/> holds everything the snapshot does.</summary>
    private static bool CoversSnapshot(
        IReadOnlyDictionary<ReplicaId, ulong> known, string versionVector)
    {
        using var document = System.Text.Json.JsonDocument.Parse(versionVector);
        if (!document.RootElement.TryGetProperty("versionVector", out var vector))
        {
            return false;
        }

        foreach (var entry in vector.EnumerateObject())
        {
            if (!Guid.TryParse(entry.Name, out var replica))
            {
                return false;
            }

            // §6: 64-bit counts travel as decimal strings, because JSON numbers
            // are doubles and stop round-tripping above 2^53.
            if (!ulong.TryParse(
                    entry.Value.GetString(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var count))
            {
                return false;
            }

            if (!known.TryGetValue(ReplicaIdConversion.FromGuid(replica), out var held)
                || held < count)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The delta, or <see langword="null"/> when it is larger than the cap and a
    /// snapshot is the cheaper answer.
    /// </summary>
    private async Task<List<(Operation Operation, long ServerSeq)>?> ReadDeltaAsync(
        Guid documentId,
        IReadOnlyDictionary<ReplicaId, ulong> known,
        CancellationToken cancellationToken)
    {
        var replicas = new Guid[known.Count];
        var sequences = new long[known.Count];
        var index = 0;
        foreach (var (replica, next) in known)
        {
            replicas[index] = ReplicaIdConversion.ToGuid(replica);
            sequences[index] = ReplicaIdConversion.ToInt64(next);
            index++;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(Delta, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = replicas, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = sequences, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });

        // One more than the cap, so exceeding it is distinguishable from
        // landing exactly on it.
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = _options.MaxDeltaOperations + 1,
            NpgsqlDbType = NpgsqlDbType.Integer,
        });

        var rows = new List<(Operation, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add((OperationMapper.FromRow(DocumentStore.ReadRow(reader, documentId)), reader.GetInt64(12)));
        }

        return rows.Count > _options.MaxDeltaOperations ? null : rows;
    }

    private async Task<long> HighWaterMarkAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            "SELECT COALESCE(MAX(server_seq), 0) FROM document_ops WHERE document_id = $1;");
        command.Parameters.AddWithValue(documentId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
