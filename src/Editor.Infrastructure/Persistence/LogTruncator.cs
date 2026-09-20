using Crdt.Core;
using Editor.Infrastructure.Observability;
using Editor.Infrastructure.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Infrastructure.Persistence;

/// <summary>Reclaims log rows for elements §5's collection has removed.</summary>
public interface ILogTruncator
{
    /// <summary>Truncates across up to <paramref name="documents"/> documents.</summary>
    Task<int> TruncateAsync(int documents, CancellationToken cancellationToken);

    /// <summary>Truncates one document; answers how many rows were removed.</summary>
    Task<int> TruncateDocumentAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>
/// The second half of §5's collection: the rows, after the elements (row 30).
/// </summary>
/// <remarks>
/// <para>
/// <b>This removes what collection collected. It does not remove a prefix,
/// and the difference is the whole design.</b> "Truncate the log behind the
/// snapshot" is the obvious reading and it is unsafe: §5 resolves a client's
/// reference by asking whether the element id is in <c>document_ops</c>, which
/// is sound only while the log is the whole truth. Delete a prefix and an
/// element created before the cut lives on in the snapshot, is visible in the
/// document, is legitimately referenceable by anyone holding it — and has no
/// row. The classifier then reads "below the frontier, author known, no
/// operation in the log" and answers <c>resync_required</c>, which tells that
/// client to destroy its unsent work because it referenced a character that is
/// on everybody's screen.
/// </para><para>
/// That is not a hypothesis. <c>LogTruncationHazardTests</c> performs the
/// prefix truncation by hand and shows the refusal, and it stays in the suite
/// as the reason this class is shaped the way it is.
/// </para><para>
/// <b>So the rows removed are exactly the ones whose elements the snapshot no
/// longer holds</b>, which is the definition of what collection collected. A
/// live element keeps its row and keeps resolving; a collected one loses its
/// row and becomes, correctly, the thing <c>resync_required</c> was written
/// for. The set is <em>derived</em> — the difference between the log's inserts
/// and the snapshot's elements — rather than recorded by the collector as it
/// works (§13.44). A recorded list can decouple from what actually happened;
/// this difference is what happened. It is also self-correcting: a sweep
/// interrupted halfway leaves the remainder for the next one.
/// </para><para>
/// <b>Delete rows are kept, and that is a cost taken deliberately.</b> §9's
/// classifier distinguishes "no such operation" from "that operation is a
/// delete", because deletes consume sequence numbers and an id naming one is a
/// client bug rather than a collection — and answering a bug with an
/// instruction to throw state away is the wrong trade. Removing collected
/// deletes' rows would reclaim more and would silently retire that narrowing,
/// so it is left alone and the cost is one row per delete. What that costs in
/// practice is measured in <c>docs/gc-reclamation.md</c> rather than assumed.
/// </para><para>
/// <b>Truncation is irreversible.</b> Collection shrinks the snapshot, which is
/// a cache of the replay and can always be rebuilt from the log; this is the
/// step that makes it permanent. It therefore refuses to act on any document
/// whose snapshot it cannot read back, and it never removes a row at or above
/// the snapshot it verified against.
/// </para>
/// </remarks>
public sealed class LogTruncator : ILogTruncator
{
    // Candidate rows: inserts the verified snapshot covers. The snapshot's
    // own server_seq bounds it, so a row appended while this sweep runs is
    // never a candidate — it is above the snapshot and was never examined.
    private const string Candidates = $"""
        SELECT o.replica_id, o.seq
        FROM document_ops AS o
        WHERE o.document_id = $1
          AND o.server_seq <= $2
          AND o.op_type = '{OperationMapper.InsertType}';
        """;

    private const string Remove = """
        DELETE FROM document_ops AS o
        USING unnest($2::uuid[], $3::bigint[]) AS gone(replica_id, seq)
        WHERE o.document_id = $1
          AND o.replica_id = gone.replica_id
          AND o.seq = gone.seq;
        """;

    private readonly EditorDbContext _context;
    private readonly NpgsqlDataSource _dataSource;
    private readonly DocumentStore _store;
    private readonly TimeProvider _time;
    private readonly InfrastructureMetrics _metrics;

    public LogTruncator(
        EditorDbContext context,
        NpgsqlDataSource dataSource,
        DocumentStore store,
        TimeProvider time,
        InfrastructureMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(metrics);

        _context = context;
        _dataSource = dataSource;
        _store = store;
        _time = time;
        _metrics = metrics;
    }

    public async Task<int> TruncateAsync(int documents, CancellationToken cancellationToken)
    {
        // Only documents with something to reclaim, oldest collection first.
        //
        // NOT the collector's "least recently examined" rotation, and the
        // difference is not an optimisation. Truncation can only remove what
        // collection removed, so a document that has not been collected since
        // it was last truncated has nothing here by construction — and
        // examining it is not free: it decodes a whole snapshot to find that
        // out. A rotation over every document spends the batch on documents
        // that cannot yield a row and reaches the ones that can only by
        // chance, which in a system with more documents than batch slots means
        // reclamation that arrives late and unpredictably.
        //
        // The stamp is LastReclaimableAt and not LastCollectedAt, which is the
        // difference between "collection removed elements here" and "a sweep
        // looked here". The collector stamps the second on every document it
        // examines, so a queue keyed on it refills itself every collection
        // sweep with documents that have nothing to give — and this sweep, which
        // decodes a snapshot per document, spends every batch on them and
        // reaches the documents that do have rows only by chance. Measured, not
        // predicted: the first version used LastCollectedAt and 121 sweeps
        // failed to reclaim a row from a document that had just been collected.
        var candidates = await _context.Documents
            .Where(row => row.LastReclaimableAt != null
                && (row.LastTruncatedAt == null || row.LastTruncatedAt < row.LastReclaimableAt))
            .OrderBy(row => row.LastReclaimableAt)
            .Select(row => row.Id)
            .Take(documents)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var removed = 0;
        foreach (var documentId in candidates)
        {
            removed += await TruncateDocumentAsync(documentId, cancellationToken)
                .ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<int> TruncateDocumentAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var document = await _context.Documents
            .SingleOrDefaultAsync(row => row.Id == documentId, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return 0;
        }

        // Stamped before the work and saved whether or not anything is removed,
        // so the stamp records that the sweep looked. Conditional on a non-zero
        // count it would park the batch on documents with nothing to reclaim.
        document.LastTruncatedAt = _time.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var (serverSeq, present) = await ReadSnapshotAsync(documentId, cancellationToken)
            .ConfigureAwait(false);

        if (present is null)
        {
            // No snapshot means no verified state to fall back on, so there is
            // nothing here that is safe to delete. A document is never
            // truncated on the strength of the log alone.
            return 0;
        }

        var candidates = await CandidatesAsync(documentId, serverSeq, cancellationToken)
            .ConfigureAwait(false);

        // The difference, which is what collection removed. Computed against
        // the snapshot that was actually decoded, not against a count or a
        // stamp: an element present here keeps its row by construction.
        var gone = new List<ElementId>();
        foreach (var id in candidates)
        {
            if (!present.Contains(id))
            {
                gone.Add(id);
            }
        }

        if (gone.Count == 0)
        {
            return 0;
        }

        var removed = await RemoveAsync(documentId, gone, cancellationToken).ConfigureAwait(false);
        _metrics.LogRowsTruncated.Add(removed);
        return removed;
    }

    /// <summary>The newest snapshot's reach and the element ids it holds.</summary>
    private async Task<(long ServerSeq, HashSet<ElementId>? Present)> ReadSnapshotAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(
            """
            SELECT server_seq, state FROM document_snapshots
            WHERE document_id = $1
            ORDER BY server_seq DESC
            LIMIT 1;
            """);

        command.Parameters.AddWithValue(documentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, null);
        }

        var serverSeq = reader.GetInt64(0);
        var state = (byte[])reader.GetValue(1);

        // Decoded rather than trusted. This is the state every truncated row is
        // being given up for, so a snapshot that will not read back is a
        // document that does not get truncated.
        var replica = SnapshotBinary.Decode(ReplicaIdConversion.FromGuid(documentId), state);

        var present = new HashSet<ElementId>();
        foreach (var element in replica.Export())
        {
            present.Add(element.Id);
        }

        return (serverSeq, present);
    }

    private async Task<List<ElementId>> CandidatesAsync(
        Guid documentId, long serverSeq, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(Candidates);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = serverSeq, NpgsqlDbType = NpgsqlDbType.Bigint });

        var rows = new List<ElementId>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ElementId(
                ReplicaIdConversion.FromGuid(reader.GetGuid(0)),
                ReplicaIdConversion.ToUInt64(reader.GetInt64(1))));
        }

        return rows;
    }

    private async Task<int> RemoveAsync(
        Guid documentId, List<ElementId> gone, CancellationToken cancellationToken)
    {
        var replicas = new Guid[gone.Count];
        var sequences = new long[gone.Count];
        for (var i = 0; i < gone.Count; i++)
        {
            replicas[i] = ReplicaIdConversion.ToGuid(gone[i].Replica);
            sequences[i] = ReplicaIdConversion.ToInt64(gone[i].Seq);
        }

        await using var command = _dataSource.CreateCommand(Remove);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = replicas, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = sequences, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
