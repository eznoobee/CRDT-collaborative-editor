using System.Collections.Concurrent;
using Crdt.Core;
using Editor.Infrastructure.Persistence;
using Npgsql;

namespace Editor.Infrastructure.Ingest;

/// <summary>
/// The per-document state ingest validation needs, cached but never owned.
/// </summary>
/// <remarks>
/// §7 makes the point explicitly for sequence numbers and §8 for everything
/// else: an app server may hold per-document state for speed and must not need
/// it for correctness after a failover. So every value here is reconstructible
/// from Postgres with one query, and losing the cache costs a query rather than
/// a wrong answer — an instance that comes up cold rejects nothing it should
/// accept and accepts nothing it should reject.
/// </remarks>
public sealed class DocumentIngestState
{
    private const string NextSequence = """
        SELECT COALESCE(MAX(seq) + 1, 0) FROM document_ops
        WHERE document_id = $1 AND replica_id = $2;
        """;

    // Live text is inserts whose element has not been tombstoned. The delete
    // rows name their target by (replica, seq), which is exactly the insert's
    // own key, so this is an anti-join rather than a document replay.
    private const string LiveBytes = """
        SELECT COALESCE(SUM(octet_length(inserted.value)), 0)
        FROM document_ops AS inserted
        WHERE inserted.document_id = $1
          AND inserted.op_type = 'ins'
          AND NOT EXISTS (
              SELECT 1 FROM document_ops AS tombstone
              WHERE tombstone.document_id = inserted.document_id
                AND tombstone.op_type = 'del'
                AND tombstone.target_replica = inserted.replica_id
                AND tombstone.target_seq = inserted.seq);
        """;

    private const string ActiveReplicas = """
        SELECT COUNT(*) FROM document_replicas
        WHERE document_id = $1 AND retired_at IS NULL;
        """;

    private readonly ConcurrentDictionary<(Guid Document, ReplicaId Replica), ulong> _nextSeq = new();
    private readonly ConcurrentDictionary<Guid, long> _liveBytes = new();
    private readonly NpgsqlDataSource _dataSource;

    public DocumentIngestState(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <summary>The sequence number this replica's next operation must carry.</summary>
    public async Task<ulong> NextSequenceAsync(
        Guid documentId, ReplicaId replicaId, CancellationToken cancellationToken)
    {
        if (_nextSeq.TryGetValue((documentId, replicaId), out var cached))
        {
            return cached;
        }

        var value = await ScalarAsync(
            NextSequence, cancellationToken, documentId, ReplicaIdConversion.ToGuid(replicaId))
            .ConfigureAwait(false);

        return _nextSeq.GetOrAdd((documentId, replicaId), ReplicaIdConversion.ToUInt64(value));
    }

    /// <summary>Live text bytes in the document.</summary>
    public async Task<long> LiveBytesAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_liveBytes.TryGetValue(documentId, out var cached))
        {
            return cached;
        }

        var value = await ScalarAsync(LiveBytes, cancellationToken, documentId).ConfigureAwait(false);
        return _liveBytes.GetOrAdd(documentId, value);
    }

    /// <summary>Replicas currently active on the document.</summary>
    /// <remarks>
    /// Not cached. It is read once per <c>negotiate</c> rather than once per
    /// operation, so the query is affordable, and a cache here would be a cap
    /// that admits the fifty-first replica whenever an instance is cold.
    /// </remarks>
    public async Task<long> ActiveReplicaCountAsync(Guid documentId, CancellationToken cancellationToken) =>
        await ScalarAsync(ActiveReplicas, cancellationToken, documentId).ConfigureAwait(false);

    /// <summary>Records a batch that was accepted and written.</summary>
    public void Accepted(Guid documentId, IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        foreach (var operation in operations)
        {
            _nextSeq[(documentId, operation.Id.Replica)] = operation.Id.Seq + 1;


            switch (operation)
            {
                case InsertOperation insert:
                    Add(documentId, insert.Value.Utf8SequenceLength);
                    break;

                case DeleteOperation:
                    // The tombstoned element's size is not known here without
                    // reading it back, and a delete can only ever shrink the
                    // document. Leaving the counter high is the safe direction:
                    // it can refuse a write that would have fit, and never
                    // admits one that would not. The next cold read corrects it.
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Forgets a document's cached state, as a failover would.</summary>
    public void Forget(Guid documentId)
    {
        _liveBytes.TryRemove(documentId, out _);

        foreach (var key in _nextSeq.Keys)
        {
            if (key.Document == documentId)
            {
                _nextSeq.TryRemove(key, out _);
            }
        }
    }

    private void Add(Guid documentId, long bytes) =>
        _liveBytes.AddOrUpdate(documentId, bytes, (_, current) => current + bytes);

    private async Task<long> ScalarAsync(
        string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = _dataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
