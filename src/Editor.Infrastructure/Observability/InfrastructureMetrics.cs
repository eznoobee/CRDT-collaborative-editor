using System.Diagnostics.Metrics;

namespace Editor.Infrastructure.Observability;

/// <summary>
/// §10's instruments for work that happens below the API.
/// </summary>
/// <remarks>
/// <para>
/// Same meter name as the API's own instruments, so a collector sees one
/// <c>Editor.Api</c> meter and not two. The split is a code-layout consequence,
/// not a taxonomy: infrastructure cannot reference the API project, and the
/// counter has to live where the work is.
/// </para><para>
/// <strong>Where the work is</strong> is the whole point (§13.31: an instrument
/// one layer above the thing it measures counts one caller rather than the
/// operation). The first version of <c>editor.gc.elements_collected</c> sat on
/// <c>TombstoneCollector</c>, the hosted service that runs the sweep on a
/// schedule — so a direct call to <see cref="Persistence.ISnapshotGarbageCollector"/>
/// destroyed elements and moved nothing. That is not a hypothetical caller:
/// 7b.8 truncates the log behind a collected snapshot and calls the collector
/// itself.
/// </para>
/// </remarks>
public sealed class InfrastructureMetrics
{
    /// <summary>The one meter for this service; see the remarks on the class.</summary>
    public const string MeterName = "Editor.Api";

    public InfrastructureMetrics(IMeterFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var meter = factory.Create(MeterName);

        ElementsCollected = meter.CreateCounter<long>(
            "editor.gc.elements_collected",
            unit: "{element}",
            description: "Tombstoned elements removed from stored snapshots by §5 collection.");
    }

    /// <summary>
    /// Elements §5 collection removed from snapshots.
    /// </summary>
    /// <remarks>
    /// The only externally visible sign that collection ran at all: §5 requires
    /// it to be invisible through the product, so a collected document and an
    /// uncollected one read identically and this counter is the difference.
    /// </remarks>
    public Counter<long> ElementsCollected { get; }
}
