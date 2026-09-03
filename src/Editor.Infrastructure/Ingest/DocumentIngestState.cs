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
    //
    // The op_type literals are interpolated from OperationMapper's constants
    // rather than written out. They were written out once, as 'ins' and 'del'
    // against a writer that stores 'insert' and 'delete', and the query matched
    // no rows for a whole phase without anything going red — see §13.16.
    private const string LiveBytes = $"""
        SELECT COALESCE(SUM(octet_length(inserted.value)), 0)
        FROM document_ops AS inserted
        WHERE inserted.document_id = $1
          AND inserted.op_type = '{OperationMapper.InsertType}'
          AND NOT EXISTS (
              SELECT 1 FROM document_ops AS tombstone
              WHERE tombstone.document_id = inserted.document_id
                AND tombstone.op_type = '{OperationMapper.DeleteType}'
                AND tombstone.target_replica = inserted.replica_id
                AND tombstone.target_seq = inserted.seq);
        """;

    // Which of the given element ids the log already holds. One query for the
    // whole batch: §8 permits validating against the referenced parent and
    // right origin, and forbids loading the document to do it.
    private const string KnownElements = $"""
        SELECT o.replica_id, o.seq
        FROM document_ops AS o
        JOIN unnest($2::uuid[], $3::bigint[]) AS wanted(replica_id, seq)
          ON o.replica_id = wanted.replica_id AND o.seq = wanted.seq
        WHERE o.document_id = $1 AND o.op_type = '{OperationMapper.InsertType}';
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

    /// <summary>
    /// Which of <paramref name="ids"/> already exist as elements in the document.
    /// </summary>
    /// <remarks>
    /// Deliberately not cached. A false negative costs a rejection the client
    /// cannot fix by retrying, and a cache that is merely stale produces
    /// exactly that — an instance that has not yet seen an element rejecting
    /// operations that reference it. The query is one round trip per batch
    /// rather than per operation, which is what makes it affordable (§8).
    /// </remarks>
    public async Task<HashSet<ElementId>> KnownElementsAsync(
        Guid documentId, IReadOnlyCollection<ElementId> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var known = new HashSet<ElementId>();
        if (ids.Count == 0)
        {
            return known;
        }

        var replicas = new Guid[ids.Count];
        var sequences = new long[ids.Count];
        var index = 0;
        foreach (var id in ids)
        {
            replicas[index] = ReplicaIdConversion.ToGuid(id.Replica);
            sequences[index] = ReplicaIdConversion.ToInt64(id.Seq);
            index++;
        }

        await using var command = _dataSource.CreateCommand(KnownElements);
        command.Parameters.AddWithValue(documentId);
        command.Parameters.AddWithValue(replicas);
        command.Parameters.AddWithValue(sequences);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            known.Add(new ElementId(
                ReplicaIdConversion.FromGuid(reader.GetGuid(0)),
                ReplicaIdConversion.ToUInt64(reader.GetInt64(1))));
        }

        return known;
    }

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
