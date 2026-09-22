using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

namespace Editor.Api.Infrastructure;

/// <summary>
/// What this instance's §10 instruments currently read.
/// </summary>
/// <remarks>
/// <para>
/// <strong>§10's instruments had no exporter at all until 7b.5.</strong> Every
/// counter and histogram was recorded and none of it could be read from outside
/// the process, which is §13.15's shape at the size of a subsystem: the metrics
/// existed completely and told nobody anything. Row 8 asks the dashboards to
/// name a broken target and the instance it broke on, and there were no
/// dashboards because there was no way to read an instance.
/// </para><para>
/// <strong>This is not a Prometheus endpoint and does not claim to be one.</strong>
/// The exporter package for it is beta at every published version, and writing
/// the text format by hand would be claiming an interoperability nothing here
/// tests — a scrape that Prometheus silently mis-parses is worse than no scrape.
/// What this serves is a JSON snapshot read by <c>scripts/dashboard.sh</c>, and
/// when the exporter ships stable it can sit beside this without either
/// pretending to be the other.
/// </para><para>
/// <strong>Histograms are reported as buckets, not percentiles.</strong> A
/// percentile computed from a running aggregate is either exact and unbounded in
/// memory or approximate and silently so. Explicit boundaries around §8's own
/// targets answer the question an operator actually has — how many submissions
/// were over 25 ms — without inventing a number.
/// </para>
/// </remarks>
public sealed class MetricsSnapshot : IDisposable
{
    /// <summary>
    /// Bucket boundaries in milliseconds, straddling §8's 25 ms target.
    /// </summary>
    /// <remarks>
    /// The boundary at 25 is the target itself, so "over target" is a
    /// subtraction rather than an interpolation. The ones below it are there so
    /// a healthy distribution is distinguishable from one sitting just under the
    /// line, which is the difference between "fine" and "about to page someone".
    /// </remarks>
    private static readonly double[] Boundaries = [1, 5, 10, 25, 50, 100, 250, 1_000];

    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, double> _sums = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram> _histograms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _gauges = new(StringComparer.Ordinal);

    public MetricsSnapshot(InstanceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EditorMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags));

        _listener.Start();
    }

    /// <summary>Which instance these readings are from.</summary>
    public InstanceIdentity Identity { get; }

    /// <summary>Serialises the current readings.</summary>
    /// <remarks>
    /// Observable instruments are pulled at the moment of the read rather than
    /// cached: a gauge whose value is whatever it last happened to be observed
    /// at is a gauge that reports the past, and the active-connection count is
    /// the one an operator reads to decide whether an instance is still serving.
    /// </remarks>
    public string ToJson()
    {
        _listener.RecordObservableInstruments();

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteString("instance", Identity.Id);
        writer.WriteString("host", Identity.Host);
        writer.WriteNumber("startedAtUnixMs", Identity.StartedAtUnixMs);
        writer.WriteNumber("uptimeSeconds", Identity.UptimeSeconds);

        writer.WriteStartObject("counters");
        foreach (var (key, value) in _sums.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("gauges");
        foreach (var (key, value) in _gauges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteNumber(key, value);
        }

        writer.WriteEndObject();

        writer.WriteStartObject("histograms");
        foreach (var (key, histogram) in _histograms.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(key);
            histogram.Write(writer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Record(
        Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = Key(instrument.Name, tags);

        if (instrument is Histogram<double> or Histogram<long> or Histogram<int>)
        {
            _histograms.GetOrAdd(key, _ => new Histogram()).Add(value);
            return;
        }

        if (instrument is ObservableGauge<double> or ObservableGauge<long> or ObservableGauge<int>)
        {
            _gauges[key] = value;
            return;
        }

        // Counters and up/down counters both accumulate; an up/down counter's
        // negative deltas make the same running total mean "current depth".
        _sums.AddOrUpdate(key, value, (_, running) => running + value);
    }

    /// <summary>
    /// The instrument name with its tags, so a tagged counter reads as the
    /// several series it actually is.
    /// </summary>
    /// <remarks>
    /// Sorted, because a rejection counter tagged in one order and read in
    /// another would appear as two series that never add up — and §10's whole
    /// reason for tagging rejections is that an operator can see which code is
    /// firing.
    /// </remarks>
    private static string Key(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return name;
        }

        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            parts.Add(string.Create(
                CultureInfo.InvariantCulture, $"{tag.Key}={tag.Value}"));
        }

        parts.Sort(StringComparer.Ordinal);
        return $"{name}{{{string.Join(",", parts)}}}";
    }

    private sealed class Histogram
    {
        private readonly Lock _gate = new();
        private readonly long[] _buckets = new long[Boundaries.Length + 1];
        private long _count;
        private double _sum;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;

        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum += value;
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);

                var bucket = Boundaries.Length;
                for (var i = 0; i < Boundaries.Length; i++)
                {
                    if (value <= Boundaries[i])
                    {
                        bucket = i;
                        break;
                    }
                }

                _buckets[bucket]++;
            }
        }

        public void Write(Utf8JsonWriter writer)
        {
            lock (_gate)
            {
                writer.WriteNumber("count", _count);
                writer.WriteNumber("sum", _sum);
                writer.WriteNumber("min", _count == 0 ? 0 : _min);
                writer.WriteNumber("max", _count == 0 ? 0 : _max);

                writer.WriteStartObject("atOrBelow");
                for (var i = 0; i < Boundaries.Length; i++)
                {
                    writer.WriteNumber(
                        Boundaries[i].ToString(CultureInfo.InvariantCulture), _buckets[i]);
                }

                writer.WriteEndObject();
                writer.WriteNumber("above", _buckets[^1]);
            }
        }
    }
}

/// <summary>
/// Which instance a reading came from (§8's multi-instance requirement).
/// </summary>
/// <remarks>
/// Row 8 asks the dashboards to name the instance a target broke on, which is
/// impossible if every instance's readings look alike. Configurable so a
/// deployment can use the name it already knows a replica by; defaulted to the
/// host and the process, so an instance that was never configured is still
/// distinguishable rather than anonymous.
/// </remarks>
public sealed class InstanceIdentity
{
    public const string Section = "Instance";

    private readonly long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>What to call this instance. Defaults to host and process id.</summary>
    public string Id { get; set; } =
        $"{Environment.MachineName}-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}";

    public string Host => Environment.MachineName;

    public long StartedAtUnixMs => _startedAt;

    public double UptimeSeconds =>
        (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _startedAt) / 1000.0;
}
