using System.Diagnostics;
using System.Globalization;
using Editor.Api.Infrastructure;
using Editor.Api.Tests.Hubs;
using Editor.Api.Tests.Infrastructure;
using Editor.Domain;

namespace Editor.Api.Tests.Load;

/// <summary>
/// §8's first target: p99 &lt; 25 ms, server-side receive → broadcast enqueue,
/// 20 concurrent editors on one document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The harness instruments nothing.</b> §8's first way a performance number
/// looks right and is not — 3b.1's near-miss, a length measured on the payload
/// rather than the frame — is answered here by reading the product's own
/// instrument rather than adding one. <c>editor.propagation.latency</c> is
/// recorded by <see cref="Editor.Api.Hubs.EditorHub"/> from the moment a batch
/// has passed §7's checks to immediately after the backplane publish, which is
/// exactly the segment §8 names. A harness that started its own stopwatch around
/// the hub call would measure the SignalR round trip as well, and would pass or
/// fail for reasons the target does not mention.
/// </para><para>
/// <b>That claim is not taken on trust.</b> §8: "A measurement is not done until
/// something has been deliberately broken and the measurement said which thing."
/// The pair that establishes this one is recorded in docs/phase-7b-report.md — a
/// delay inside the segment must move the number, and a delay outside it,
/// between the latency record and the method returning, must not. The second is
/// the stronger of the two: it is what distinguishes this from a harness that
/// happens to be measuring the whole call.
/// </para><para>
/// <b>In-process, and the utilisation says what that costs.</b> The twenty
/// editors and the server share one process, so every cycle the generator spends
/// is one the server does not have. That makes a pass conservative — the server
/// was competing with its own load generator — and a saturated run meaningless,
/// which is why the utilisation is part of the result and is checked.
/// </para>
/// </remarks>
[Collection(nameof(EditorTests))]
public sealed class PropagationLatencyMeasurement
{
    private readonly EditorFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PropagationLatencyMeasurement(EditorFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private void Report(string line)
    {
        _output.WriteLine(line);
        Results.Write(line);
    }

    /// <summary>§8's stated load for this target.</summary>
    private const int Editors = 20;

    /// <summary>
    /// Batches per second per editor, for the run that measures §8's target.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Eight, because §8's target says "20 concurrent editors" and an editor
    /// is a person.</b> A fast typist sustains six to eight characters a second.
    /// Twenty clients submitting in a tight loop is not twenty editors — it is
    /// twenty load generators, and the first version of this measurement drove
    /// 786 batches a second and reported the queueing that produced as though it
    /// were what twenty people cost.
    /// </para><para>
    /// The saturating run is still reported, as a separate figure with its own
    /// name: it is a capacity number, and §8's target is not about capacity.
    /// </para>
    /// </remarks>
    private const double PacedBatchesPerSecondPerEditor = 8;

    /// <summary>
    /// Batches each editor submits.
    /// </summary>
    /// <remarks>
    /// Chosen so the total clears <see cref="Percentiles.MinimumSamples"/> with
    /// room to spare: at twenty editors, fifty each is a thousand samples, which
    /// is the point at which a p99 has ten observations above it rather than
    /// being the single worst.
    /// </remarks>
    private const int BatchesPerEditor = 60;

    /// <summary>§8's target: twenty people typing.</summary>
    [Fact]
    public Task Receive_to_broadcast_enqueue_p99_at_editor_pace() =>
        MeasureAsync("paced", PacedBatchesPerSecondPerEditor, assertTarget: true);

    /// <summary>
    /// The same segment with the throttle off, reported and not asserted.
    /// </summary>
    /// <remarks>
    /// What the server does when twenty connections submit as fast as they are
    /// answered. It is a capacity figure and §8 states no target for it; it is
    /// here because the difference between the two runs is the only thing that
    /// says whether the paced number has any headroom.
    /// </remarks>
    [Fact]
    public Task Receive_to_broadcast_enqueue_saturated() =>
        MeasureAsync("saturating", batchesPerSecondPerEditor: 0, assertTarget: false);

    /// <summary>
    /// The same offered load spread over twenty documents instead of one.
    /// </summary>
    /// <remarks>
    /// <b>A discriminator, not a target.</b> §8 states no figure for this. It
    /// exists because the paced run puts its whole tail in one stage, and the
    /// two explanations for that — contention on the single document every
    /// editor is writing to, or scheduling delay from a load generator sharing
    /// the server's process — are indistinguishable from the number alone. This
    /// run holds the generator, the rate and the sample count fixed and removes
    /// only the shared document. Whichever way it moves, one of the two
    /// explanations is gone.
    /// </remarks>
    [Fact]
    public Task Receive_to_broadcast_enqueue_across_documents() =>
        MeasureAsync(
            "paced, one document each",
            PacedBatchesPerSecondPerEditor,
            assertTarget: false,
            documentPerEditor: true);

    /// <summary>
    /// The load curve, under both batching policies (9.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a curve and not two points.</b> Adaptive flushing is defined by
    /// what the queue holds when a write finishes, so its behaviour at idle and
    /// its behaviour at saturation are the two ends of one continuum and neither
    /// says anything about the middle. A scheme that switched modes at a
    /// threshold would have a cliff somewhere between them, and measuring only
    /// the ends is exactly how such a cliff goes unnoticed until it is in
    /// production.
    /// </para><para>
    /// Eight batches a second per editor is §8's own scenario — a fast typist —
    /// and it appears in the middle of this range rather than at its end, which
    /// is the point: the target is not measured at the edge of what the server
    /// can do.
    /// </para><para>
    /// <b>Both policies, same build, same run.</b> §8 requires a number to be
    /// reported with the build that produced it, and the comparison this task
    /// turns on is between two settings rather than two builds — so they are
    /// measured on one, alternating, rather than across two runs whose
    /// difference could be anything the machine was doing.
    /// </para><para>
    /// Reported, never asserted. §8 states a target for one point on this curve
    /// and the assertion for it lives on
    /// <see cref="Receive_to_broadcast_enqueue_p99_at_editor_pace"/>; a
    /// threshold invented for the rest would be a number nobody chose.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Receive_to_broadcast_enqueue_across_the_load_curve()
    {
        foreach (var rate in (double[])[1, 2, 4, 8, 16, 32])
        {
            foreach (var windowMs in (int[])[0, 50])
            {
                await MeasureAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"curve {rate:0.#}/s/editor, {(windowMs == 0 ? "adaptive" : $"{windowMs} ms window")}"),
                    rate,
                    assertTarget: false,
                    windowMs: windowMs);
            }
        }
    }

    private async Task MeasureAsync(
        string label,
        double batchesPerSecondPerEditor,
        bool assertTarget,
        bool documentPerEditor = false,
        int? windowMs = null)
    {
        LoadGate.Require();
        _fixture.RequireBoth();

        var provenance = Provenance.Current();
        using var metrics = new MetricCollector(EditorMetrics.MeterName);

        // 7b.2's stages, which exist for exactly this: the aggregate says the
        // segment is slow and cannot say which part of it is. A number without
        // an attribution is a number nobody can act on.
        using var trace = new ActivityCollector(EditorTracing.SourceName);
        await using var factory = windowMs is null
            ? new EditorApiFactory(_fixture)
            : new EditorApiFactory(
                _fixture,
                settings: new()
                {
                    ["Batching:WindowMs"] = windowMs.Value.ToString(CultureInfo.InvariantCulture),
                });

        var owner = string.Create(CultureInfo.InvariantCulture, $"load-p99-owner-{label.GetHashCode(StringComparison.Ordinal):x}");
        var shared = await DocumentSetup.DocumentAsync(factory, owner);

        var clients = new List<DocumentClient>(Editors);
        try
        {
            for (var i = 0; i < Editors; i++)
            {
                var subject = string.Create(CultureInfo.InvariantCulture, $"{owner}-{i}");
                var documentId = documentPerEditor
                    ? await DocumentSetup.DocumentAsync(factory, subject)
                    : shared;

                await DocumentSetup.GrantAsync(factory, documentId, subject, Role.Editor);
                clients.Add(await DocumentClient.JoinAsync(factory, subject, documentId));
            }

            // Warm first, and not counted. The first submission on a connection
            // pays for the document's first load, EF's first query plan and the
            // JIT, none of which a p99 over a working server should include —
            // and all of which would otherwise land in the tail this target is
            // about.
            foreach (var client in clients)
            {
                Assert.Null((await client.SubmitAsync(client.Writer.Type("w"))).Code);
            }

            var warmed = metrics.Count("editor.propagation.latency");

            var cpu = Process.GetCurrentProcess().TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            var interval = batchesPerSecondPerEditor <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(1 / batchesPerSecondPerEditor);

            await Task.WhenAll(clients.Select(async client =>
            {
                for (var batch = 0; batch < BatchesPerEditor; batch++)
                {
                    // Paced against the start of the run rather than by sleeping
                    // after each submission: sleeping adds the server's own
                    // response time to the gap, so a slow server would quietly
                    // reduce the offered load and flatter itself.
                    if (interval > TimeSpan.Zero)
                    {
                        var due = interval * batch;
                        var wait = due - wall.Elapsed;
                        if (wait > TimeSpan.Zero)
                        {
                            await Task.Delay(wait);
                        }
                    }

                    var result = await client.SubmitAsync(client.Writer.Type("x"));
                    Assert.Null(result.Code);
                }
            }));

            wall.Stop();
            var utilisation = Utilisation.Since(cpu, wall);

            // Dropping the warm-up by count rather than by clearing the
            // collector, so a sample arriving late from a warm-up submission
            // cannot be counted as a measured one.
            var samples = metrics.Samples("editor.propagation.latency").Skip(warmed).ToList();
            var latency = Percentiles.Of(samples);

            Report($"§8 target 1 — p99 receive → broadcast enqueue < 25 ms [{label}]");
            Report($"  build      {provenance}");
            Report(string.Create(
                CultureInfo.InvariantCulture,
                $"  load       {Editors} editors × {BatchesPerEditor} batches on "
                + $"{(documentPerEditor ? $"{Editors} documents" : "one document")}, "
                + $"{(interval > TimeSpan.Zero ? $"{batchesPerSecondPerEditor:F0}/s each" : "unthrottled")}"));
            Report($"  latency ms {latency}");
            Report($"  generator  {utilisation}");

            foreach (var stage in new[]
            {
                EditorTracing.Validate, EditorTracing.Persist, EditorTracing.Broadcast,
            })
            {
                var durations = trace.Named(stage)
                    .Select(activity => activity.Duration.TotalMilliseconds)
                    .Skip(warmed)
                    .ToList();

                Report($"  {stage,-18} {Percentiles.Of(durations)}");
            }

            Report(string.Create(
                CultureInfo.InvariantCulture,
                $"  throughput {samples.Count / wall.Elapsed.TotalSeconds:F0} batches/s over {wall.Elapsed.TotalSeconds:F1}s"));

            // The sample count is checked, not just printed. §8 makes it part of
            // the result, and a result that silently shrank to 200 samples is a
            // p99 that quietly became the worst of 200.
            Assert.True(
                latency.Count >= Percentiles.MinimumSamples,
                $"a p99 over {latency.Count} samples is decoration; §8 wants at least {Percentiles.MinimumSamples}");

            Assert.False(
                utilisation.IsSaturated,
                $"the box was saturated ({utilisation}), so this number describes the box and not the server");

            if (assertTarget)
            {
                Assert.True(
                    latency.P99 < 25,
                    $"§8's target is p99 < 25 ms; measured {latency}");
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }
}
