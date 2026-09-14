using System.Diagnostics.Metrics;

namespace Editor.Api.Infrastructure;

/// <summary>§10's metric list, as instruments that must exist and must move.</summary>
/// <remarks>
/// <para>
/// <strong>A registered counter that is never incremented satisfies "the metric
/// exists" completely</strong> — §13.15's shape, which this project has already
/// hit twice. So every name here is tested by making it move, and the ones whose
/// only states are zero and non-zero are tested in pairs: a case where they move
/// and a case where they do not. "Non-zero" is otherwise indistinguishable from
/// "always non-zero".
/// </para><para>
/// <strong>§13.32 applies to the list itself.</strong> A metric keyed on
/// submission covers writers and nobody else, which would show a document being
/// read as a document nobody is using — the same blind spot that let one viewer
/// hold the stability frontier still for a whole phase. Hence
/// the active-connection gauge being a connection count rather than a submitter
/// count.
/// </para>
/// </remarks>
public sealed class EditorMetrics : IDisposable
{
    /// <summary>The meter name a collector subscribes to.</summary>
    public const string MeterName = "Editor.Api";

    private readonly Meter _meter;

    public EditorMetrics(
        IMeterFactory factory,
        Editor.Api.Hubs.DocumentConnections connections,
        Editor.Api.Documents.ISnapshotAge snapshots,
        IStateReadings state)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(state);

        _meter = factory.Create(MeterName);

        // Observable, because the truth is a live count rather than a running
        // total: an incrementing counter of connections opened cannot answer
        // "how many are open now", which is the question §8's 1,000-per-instance
        // target is about.
        _meter.CreateObservableGauge(
            "editor.connections.active",
            () => connections.Held().Count,
            unit: "{connection}",
            description: "Connections this instance is holding.");

        // §10's snapshot age. Absent until 7b.3 for register row 28's reason:
        // nothing in the running server took a periodic snapshot, so the only
        // snapshots that existed were the collector's, and a gauge over those
        // would have reported the age of something that is not what §10 means.
        // Now that the sweep exists, this is the number that says whether it is
        // keeping up.
        _meter.CreateObservableGauge(
            "editor.snapshot.age",
            () => snapshots.WorstSnapshotAge.TotalSeconds,
            unit: "s",
            description: "Oldest latest-snapshot across the documents the last sweep examined.");

        // §10's state-derived readings. Row 8's finding in its general form:
        // a counter beside a write measures that control reached the line after
        // it, and these measure the rows themselves. They are identical on
        // every instance by construction — one database — so they detect and do
        // not localise. See StateReadings and docs/section-10-audit.md.
        _meter.CreateObservableGauge(
            "editor.replicas.live",
            () => state.LiveReplicas,
            unit: "{replica}",
            description: "Replicas not yet retired, counted from the rows (§5).");

        _meter.CreateObservableGauge(
            "editor.replicas.silent",
            () => state.SilentReplicas,
            unit: "{replica}",
            description: "Live replicas that have never said what they hold (§5).");

        _meter.CreateObservableGauge(
            "editor.replicas.retired.stored",
            () => state.RetiredReplicas,
            unit: "{replica}",
            description: "Replicas carrying a retirement timestamp, counted from the rows (§5).");

        _meter.CreateObservableGauge(
            "editor.snapshots.stored",
            () => state.StoredSnapshots,
            unit: "{snapshot}",
            description: "Snapshot rows stored, counted from the rows (§6).");

        OperationsReceived = _meter.CreateCounter<long>(
            "editor.operations.received",
            unit: "{operation}",
            description: "Operations arriving at ingest, before validation.");

        OperationsApplied = _meter.CreateCounter<long>(
            "editor.operations.applied",
            unit: "{operation}",
            description: "Operations committed to the log and broadcast.");

        OperationsRejected = _meter.CreateCounter<long>(
            "editor.operations.rejected",
            unit: "{batch}",
            description: "Batches refused, tagged with §7's rejection code.");

        // Received minus applied is not the rejected count and must not be read
        // as one: a refusal rejects a whole batch, and the operations in it were
        // never applied individually. The unit on each says which is which.
        ResyncRequired = _meter.CreateCounter<long>(
            "editor.resync_required",
            unit: "{response}",
            description: "Refusals telling a client its state is unrecoverable (§9).");

        ReplicasRetired = _meter.CreateCounter<long>(
            "editor.replicas.retired",
            unit: "{replica}",
            description: "Replicas retired after T_retire of inactivity (§5).");

        // §10 requires presence, catch-up and §5's acknowledgement timer to each
        // be represented, and §13.32 is why: the gauge above covers presence,
        // but a viewer who never submits is otherwise represented by nothing
        // that moves. These two are the reader's traffic.
        CatchUps = _meter.CreateCounter<long>(
            "editor.catchup.requests",
            unit: "{request}",
            description: "Catch-up reads, tagged with whether a snapshot was served.");

        // RENAMED, and the name is the fix. It counts acknowledgement REQUESTS
        // reaching the end of the hub method — which is a legitimate question,
        // and is not the question the old name answered. Row 8 broke the write
        // and this counter did not move, because it never measured the write.
        // What does is editor.replicas.silent, above.
        AcknowledgementsReceived = _meter.CreateCounter<long>(
            "editor.acknowledgements.received",
            unit: "{request}",
            description: "§5 acknowledgement requests handled, tagged with what prompted them.");

        // §8's backpressure drop, which until 7b.5 was counted on
        // DocumentBroadcaster and readable nowhere. §13.15 names this exact
        // instrument as the one that matters: dropping a slow client and never
        // dropping one produce the same document, so the count is the only
        // thing that tells them apart — and a count nobody can read tells them
        // apart for nobody.
        BackpressureDrops = _meter.CreateCounter<long>(
            "editor.backpressure.drops",
            unit: "{connection}",
            description: "Connections closed for not keeping up with the fan-out (§8).");

        PropagationLatency = _meter.CreateHistogram<double>(
            "editor.propagation.latency",
            unit: "ms",
            description: "Receive to broadcast enqueue, the segment §8 names.");

        OutboundQueueDepth = _meter.CreateUpDownCounter<long>(
            "editor.outbound.queue_depth",
            unit: "{message}",
            description: "Messages waiting on a connection's bounded channel (§8).");
    }

    public Counter<long> OperationsReceived { get; }

    public Counter<long> OperationsApplied { get; }

    public Counter<long> OperationsRejected { get; }

    public Counter<long> ResyncRequired { get; }

    public Counter<long> ReplicasRetired { get; }

    /// <summary>
    /// Connections dropped for backpressure (§8).
    /// </summary>
    /// <remarks>
    /// A rate above zero is a network or a client problem that is otherwise
    /// invisible until someone complains their editor keeps reconnecting.
    /// </remarks>
    public Counter<long> BackpressureDrops { get; }

    /// <summary>Catch-up reads, tagged <c>source</c>: snapshot or log.</summary>
    public Counter<long> CatchUps { get; }

    /// <summary>
    /// §5 acknowledgement <em>requests</em> handled, tagged <c>via</c>: timer,
    /// catchup or submit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Requests, not writes, and the name says so since 7b.5.</strong>
    /// Row 8 removed the frontier write and left this increment standing: it
    /// read identically on the broken instance and the healthy one. It was
    /// never a measurement of the write — it measures that the hub method got
    /// to its last line — and under the old name <c>editor.acknowledgements</c>
    /// it claimed otherwise on every dashboard.
    /// </para><para>
    /// Kept, because request volume by source is a real question: a client whose
    /// timer has stopped still catches up once per connection, so
    /// <c>via=timer</c> at zero with traffic is a client-side fault this is the
    /// only view of. For "did the frontier actually move", read
    /// <c>editor.replicas.silent</c>, which is counted from the rows.
    /// </para>
    /// </remarks>
    public Counter<long> AcknowledgementsReceived { get; }

    public Histogram<double> PropagationLatency { get; }

    /// <summary>
    /// §10 calls this "pending-buffer depth"; the server has no pending buffer.
    /// </summary>
    /// <remarks>
    /// §5 bounds a pending set because origins are client-supplied, and that is
    /// true of a peer receiving a broadcast. It is not true at ingest:
    /// <see cref="Editor.Infrastructure.Ingest.IngestValidator"/> rejects a
    /// non-ready operation rather than buffering one, deliberately, because
    /// buffering an id that may never arrive is the denial of service §5 warns
    /// about — so there is no server-side pending buffer to report a depth for.
    /// <para>
    /// Reporting zero under §10's name would be worse than reporting nothing: a
    /// dashboard would show a queue that is always empty and an operator would
    /// conclude the ingest path is never backed up. What the server does have is
    /// the bounded outbound channel §8 requires, and that is a real backlog with
    /// real consequences, so it is measured under its own name. The divergence
    /// is recorded in §10 rather than papered over.
    /// </para>
    /// </remarks>
    public UpDownCounter<long> OutboundQueueDepth { get; }

    public void Dispose() => _meter.Dispose();
}
