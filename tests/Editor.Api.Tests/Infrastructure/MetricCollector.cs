using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Editor.Api.Tests.Infrastructure;

/// <summary>
/// Records every measurement one meter publishes, so a test can assert a metric
/// <em>moved</em> rather than that it exists.
/// </summary>
/// <remarks>
/// Written rather than taken from a package because the assertion this project
/// needs is narrow: §13.15's failure is a counter registered and never
/// incremented, and what catches it is reading the values, not the instrument
/// list. Observable instruments are read on demand through
/// <see cref="Observe"/>, because a gauge publishes nothing until someone asks.
/// </remarks>
public sealed class MetricCollector : IDisposable
{
    private readonly MeterListener _listener = new();
    // A queue, not a bag: ConcurrentBag does not preserve insertion order, so
    // Latest() over one returns an arbitrary sample. That produced a gauge test
    // reading the value from BEFORE the connection opened and reporting that
    // the gauge had not moved.
    private readonly ConcurrentQueue<Measurement> _measurements = new();

    /// <param name="meterName">The meter to listen to.</param>
    /// <param name="scope">
    /// The <see cref="Meter.Scope"/> to restrict to, or null for every meter of
    /// that name in the process.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Scope matters for gauges and only for gauges.</b> A counter's
    /// <see cref="Total"/> sums what this test caused, and another live host
    /// contributing to the same meter name adds to it visibly. An observable
    /// gauge is different: <see cref="Observe"/> pulls <em>every</em> published
    /// instrument with that name, so one call records one sample per live host,
    /// and <see cref="Latest"/> then returns whichever host happened to be
    /// enumerated last — which may be a host that has never heard of this
    /// test's connection.
    /// </para><para>
    /// That is what made the active-connection gauge test intermittent: it did
    /// not race on the value, it read a different host's value. Passing this
    /// test's own <c>factory.Services</c> narrows the listener to the meter
    /// <c>IMeterFactory</c> created for that host, which makes the reading
    /// independent of how many other tests are mid-flight.
    /// </para>
    /// </remarks>
    public MetricCollector(string meterName, object? scope = null)
    {
        ArgumentNullException.ThrowIfNull(meterName);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName
                && (scope is null || ReferenceEquals(instrument.Meter.Scope, scope)))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument.Name, value, tags));

        _listener.Start();
    }

    /// <summary>Pulls the current value of every observable instrument.</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    /// <summary>The total recorded against one instrument.</summary>
    public double Total(string instrument) =>
        _measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

    /// <summary>The total recorded against one instrument carrying one tag.</summary>
    public double Total(string instrument, string tag, object? value) =>
        _measurements
            .Where(m => m.Instrument == instrument)
            .Where(m => m.Tags.Any(t => t.Key == tag && Equals(t.Value, value)))
            .Sum(m => m.Value);

    /// <summary>How many separate measurements one instrument received.</summary>
    public int Count(string instrument) =>
        _measurements.Count(m => m.Instrument == instrument);

    /// <summary>
    /// Every value recorded against one instrument, in the order they arrived.
    /// </summary>
    /// <remarks>
    /// For a histogram, where the distribution is the point: §8's percentiles
    /// cannot be recovered from a sum and a count. Arrival order is what lets a
    /// caller drop a warm-up prefix by count rather than by clearing the
    /// collector, which would race a sample still in flight.
    /// </remarks>
    public IReadOnlyList<double> Samples(string instrument) =>
        [.. _measurements.Where(m => m.Instrument == instrument).Select(m => m.Value)];

    /// <summary>The most recent value observed for an instrument.</summary>
    public double Latest(string instrument) =>
        _measurements.Where(m => m.Instrument == instrument).Select(m => m.Value).LastOrDefault();

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue(new Measurement(instrument, value, [.. tags]));

    private sealed record Measurement(
        string Instrument, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
