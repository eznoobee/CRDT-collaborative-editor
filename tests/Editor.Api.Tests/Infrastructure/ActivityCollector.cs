using System.Collections.Concurrent;
using System.Diagnostics;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// Captures completed activities from one source, in the order they finished.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="ActivityListener"/> rather than an exporter, for the same
/// reason <c>MetricCollector</c> uses a <see cref="System.Diagnostics.Metrics.MeterListener"/>:
/// it observes the instrumentation the product actually emits, so a test cannot
/// pass against spans the test infrastructure invented.
/// </para><para>
/// <strong>Nothing is sampled out.</strong> With no listener, or a listener that
/// declines, <c>StartActivity</c> returns null and every span in the product
/// silently disappears — which is a trace test that passes because there is
/// nothing to disagree with. <see cref="ActivitySamplingResult.AllDataAndRecorded"/>
/// is what makes the assertions about the product rather than about sampling.
/// </para>
/// </remarks>
public sealed class ActivityCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _finished = new();

    public ActivityCollector(string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _finished.Enqueue(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Completed activities, oldest first.</summary>
    public IReadOnlyList<Activity> Finished => [.. _finished];

    /// <summary>Completed activities with this operation name, oldest first.</summary>
    public IReadOnlyList<Activity> Named(string name) =>
        [.. _finished.Where(activity => activity.OperationName == name)];

    /// <summary>The one activity with this name, or a failure naming what was seen.</summary>
    public Activity Only(string name)
    {
        var matches = Named(name);
        Assert.True(
            matches.Count == 1,
            $"expected one {name}, saw {matches.Count}: [{string.Join(", ", Finished.Select(a => a.OperationName))}]");

        return matches[0];
    }

    public void Dispose() => _listener.Dispose();
}
