using Crdt.Core;
using Editor.Infrastructure.Observability;
using Npgsql;
using NpgsqlTypes;

namespace Editor.Infrastructure.Persistence;

/// <summary>How far one document's log has run past its latest snapshot.</summary>
/// <param name="DocumentId">The document.</param>
/// <param name="HeadServerSeq">The highest sequence in its log.</param>
/// <param name="SnapshotServerSeq">The sequence of its latest snapshot, or zero.</param>
/// <param name="SnapshotAge">
/// How long ago that snapshot was written. §10's "snapshot age"; for a document
/// that has never been snapshotted it is measured from the document's own
/// creation, because "no snapshot" is the worst case rather than the absence of
/// one.
/// </param>
public readonly record struct SnapshotLag(
    Guid DocumentId, long HeadServerSeq, long SnapshotServerSeq, TimeSpan SnapshotAge)
{
    /// <summary>Operations written since the latest snapshot.</summary>
    public long Operations => HeadServerSeq - SnapshotServerSeq;
}

/// <summary>§6's periodic snapshot, taken by the running server.</summary>
public interface IPeriodicSnapshotter
{
    /// <summary>
    /// Snapshots the documents furthest behind, up to <paramref name="documents"/>.
    /// </summary>
    /// <returns>What the sweep saw, whether or not it wrote anything.</returns>
    Task<SnapshotSweep> SnapshotAsync(int documents, CancellationToken cancellationToken);

    /// <summary>Snapshots one document if it is due; answers whether it wrote.</summary>
    Task<bool> SnapshotDocumentAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>What one sweep wrote and what it saw.</summary>
/// <param name="Written">Documents snapshotted.</param>
/// <param name="WorstAge">
/// The largest snapshot age among the documents examined, or zero when none
/// were. §10's gauge reads this rather than querying per scrape.
/// </param>
public readonly record struct SnapshotSweep(int Written, TimeSpan WorstAge);

/// <summary>
/// Writes §6's periodic snapshot for the documents furthest behind.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is register row 28.</strong> §6 specified a snapshot every 500
/// operations, <see cref="SnapshotPolicy"/> and
/// <see cref="DocumentStore.SaveSnapshotAsync"/> were implemented correctly, and
/// nothing in the running server called either — so every document was rebuilt
/// by full replay of its log, and the only snapshot the product ever stored was
/// the collector's. A subsystem specified, built, and never installed.
/// </para><para>
/// <strong>Background rather than inline, and that changed the predicate.</strong>
/// §8 puts snapshot compaction in a background service, and it is right to: a
/// snapshot is a full replay plus a write, and taking one on the submission that
/// happens to cross the threshold would put it inside the segment §8's p99
/// target measures. The original predicate — did this batch cross a multiple of
/// N — only works for a caller that sees each batch. A sweep sees documents, not
/// batches, so the predicate here is <em>how far the log has run past the last
/// snapshot</em>. It is also self-healing where the crossing test is not: a
/// missed crossing, from a restart or a lost batch, left a document unsnapshotted
/// for another N operations, while a gap only widens until something closes it.
/// </para><para>
/// Ranked by that gap rather than by least-recently-examined, because unlike
/// collection there is a correct order here: the document furthest behind is the
/// one whose load costs most, and §8's document-load target is what this exists
/// to defend.
/// </para>
/// </remarks>
public sealed class PeriodicSnapshotter : IPeriodicSnapshotter
{
    // Ranked, not filtered: the policy's threshold is applied in C# so the rule
    // lives in one place. A WHERE clause repeating it here would be a second
    // copy to keep in step, and the two disagreeing is a document that is
    // selected and then declined every sweep, forever.
    private const string Laggards = """
        SELECT d.id,
               COALESCE(o.head, 0) AS head,
               COALESCE(s.seq, 0) AS snapshot_seq,
               COALESCE(s.created_at, d.created_at) AS since
        FROM documents d
        LEFT JOIN LATERAL (
            SELECT MAX(server_seq) AS head FROM document_ops WHERE document_id = d.id
        ) o ON TRUE
        LEFT JOIN LATERAL (
            SELECT server_seq AS seq, created_at FROM document_snapshots
            WHERE document_id = d.id
            ORDER BY server_seq DESC
            LIMIT 1
        ) s ON TRUE
        WHERE d.deleted_at IS NULL
        ORDER BY COALESCE(o.head, 0) - COALESCE(s.seq, 0) DESC
        LIMIT $1;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly DocumentStore _store;
    private readonly SnapshotPolicy _policy;
    private readonly TimeProvider _time;
    private readonly InfrastructureMetrics _metrics;

    public PeriodicSnapshotter(
        NpgsqlDataSource dataSource,
        DocumentStore store,
        SnapshotPolicy policy,
        TimeProvider time,
        InfrastructureMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(metrics);

        _dataSource = dataSource;
        _store = store;
        _policy = policy;
        _time = time;
        _metrics = metrics;
    }

    public async Task<SnapshotSweep> SnapshotAsync(
        int documents, CancellationToken cancellationToken)
    {
        var lags = await LaggardsAsync(documents, cancellationToken).ConfigureAwait(false);

        var written = 0;
        var worst = TimeSpan.Zero;

        foreach (var lag in lags)
        {
            // Age is reported for every document examined, not only the ones
            // written: a document that is up to date has a young snapshot and
            // belongs in the maximum, or the gauge would report the age of
            // whatever happened to be stale and never fall.
            if (lag.SnapshotAge > worst)
            {
                worst = lag.SnapshotAge;
            }

            if (!_policy.IsDue(lag.SnapshotServerSeq, lag.HeadServerSeq))
            {
                // Ranked by gap, so the first document that is not due means
                // none of the rest are either.
                break;
            }

            if (await WriteAsync(lag.DocumentId, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        return new SnapshotSweep(written, worst);
    }

    public async Task<bool> SnapshotDocumentAsync(
        Guid documentId, CancellationToken cancellationToken)
    {
        var lag = await LagAsync(documentId, cancellationToken).ConfigureAwait(false);

        return lag is not null
            && _policy.IsDue(lag.Value.SnapshotServerSeq, lag.Value.HeadServerSeq)
            && await WriteAsync(documentId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        // Loaded as the document's own id, which is what every non-authoring
        // reader uses. A snapshot records state, and state has no author.
        var (replica, serverSeq) = await _store
            .LoadAtHeadAsync(
                documentId, ReplicaIdConversion.FromGuid(documentId), cancellationToken)
            .ConfigureAwait(false);

        if (serverSeq == 0)
        {
            return false;
        }

        await _store
            .SaveSnapshotAsync(documentId, replica, serverSeq, cancellationToken)
            .ConfigureAwait(false);

        _metrics.SnapshotsWritten.Add(1);
        return true;
    }

    private async Task<List<SnapshotLag>> LaggardsAsync(
        int documents, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(Laggards, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documents, NpgsqlDbType = NpgsqlDbType.Integer });

        var now = _time.GetUtcNow();
        var lags = new List<SnapshotLag>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lags.Add(Read(reader, now));
        }

        return lags;
    }

    private async Task<SnapshotLag?> LagAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = new NpgsqlCommand(OneDocument, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId, NpgsqlDbType = NpgsqlDbType.Uuid });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Read(reader, _time.GetUtcNow())
            : null;
    }

    private static SnapshotLag Read(NpgsqlDataReader reader, DateTimeOffset now)
    {
        var since = reader.GetFieldValue<DateTimeOffset>(3);

        // Clamped at zero. A snapshot written by an instance whose clock runs
        // ahead would otherwise produce a negative age, and a gauge that can go
        // negative is one an operator stops believing.
        var age = now > since ? now - since : TimeSpan.Zero;

        return new SnapshotLag(
            reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), age);
    }

    private const string OneDocument = """
        SELECT d.id,
               COALESCE(o.head, 0) AS head,
               COALESCE(s.seq, 0) AS snapshot_seq,
               COALESCE(s.created_at, d.created_at) AS since
        FROM documents d
        LEFT JOIN LATERAL (
            SELECT MAX(server_seq) AS head FROM document_ops WHERE document_id = d.id
        ) o ON TRUE
        LEFT JOIN LATERAL (
            SELECT server_seq AS seq, created_at FROM document_snapshots
            WHERE document_id = d.id
            ORDER BY server_seq DESC
            LIMIT 1
        ) s ON TRUE
        WHERE d.id = $1 AND d.deleted_at IS NULL;
        """;
}
