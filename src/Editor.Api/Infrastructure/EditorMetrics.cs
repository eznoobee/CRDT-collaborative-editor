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

    public EditorMetrics(IMeterFactory factory, Editor.Api.Hubs.DocumentConnections connections)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(connections);

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

        Acknowledgements = _meter.CreateCounter<long>(
            "editor.acknowledgements",
            unit: "{acknowledgement}",
            description: "§5 acknowledgements written, tagged with what prompted them.");

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

    /// <summary>Catch-up reads, tagged <c>source</c>: snapshot or log.</summary>
    public Counter<long> CatchUps { get; }

    /// <summary>
    /// §5 acknowledgements written, tagged <c>via</c>: timer or catchup.
    /// </summary>
    /// <remarks>
    /// <strong>The tag is the point.</strong> An untagged count is satisfied by
    /// the catch-up piggyback alone, and a client whose acknowledgement timer
    /// has stopped still catches up once per connection — so the count stays
    /// healthy while the stability frontier stops moving, which is precisely the
    /// failure that held it still for a whole phase. Split by what prompted it,
    /// "timer at zero" is visible.
    /// </remarks>
    public Counter<long> Acknowledgements { get; }

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

    /// <summary>
    /// §10's "snapshot age" is not here, and the reason is register row 28.
    /// </summary>
    /// <remarks>
    /// Nothing in the running server takes a periodic snapshot — `SnapshotPolicy`
    /// and `SaveSnapshotAsync` are implemented and called only from tests — so
    /// the only snapshots that exist are the collector's, written when it
    /// collects. A gauge over those would report the age of something that is
    /// not the thing §10 means, and a gauge reporting "no snapshot" forever
    /// would be read as a broken exporter rather than as a missing subsystem.
    /// It lands with row 28, which is where the subject arrives.
    /// </remarks>
    public void Dispose() => _meter.Dispose();
}
