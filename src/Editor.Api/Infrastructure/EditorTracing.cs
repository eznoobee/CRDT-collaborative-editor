using System.Diagnostics;

namespace Editor.Api.Infrastructure;

/// <summary>
/// §10's trace of one submission: receive → validate → persist → broadcast.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The stages exist because the aggregate does not localise anything.</strong>
/// <c>editor.propagation.latency</c> says a submission took 40ms; it cannot say
/// whether that was Postgres, the fan-out, or the backplane, and those have
/// nothing in common as remedies. §8's p99 target is judged on the aggregate and
/// diagnosed on the breakdown, so the breakdown is the part that has to exist
/// before 7b.5's dashboards are asked to find anything.
/// </para><para>
/// <strong>What makes a trace non-vacuous.</strong> A span that exists and a
/// span that is related to its neighbours are different things, and only the
/// second is a trace: four unparented activities carry the same information as
/// four log lines. So the stages are started under the submission's activity and
/// the tests assert the parentage, not the presence.
/// </para><para>
/// A rejected submission gets a root and whichever stages it reached — which is
/// the diagnostic value, since "where did it stop" is the question — and its
/// status carries the §7 code that stopped it.
/// </para>
/// </remarks>
public static class EditorTracing
{
    /// <summary>The source a collector subscribes to; matches the meter name.</summary>
    public const string SourceName = "Editor.Api";

    /// <summary>One submission, from arrival to broadcast enqueue.</summary>
    public const string Submit = "editor.submit";

    /// <summary>§5's readiness and §7's caps, including run expansion (3.6).</summary>
    public const string Validate = "editor.validate";

    /// <summary>The append to the operation log.</summary>
    public const string Persist = "editor.persist";

    /// <summary>Local fan-out and the backplane publish (§8).</summary>
    public const string Broadcast = "editor.broadcast";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts the submission's root activity.</summary>
    public static Activity? StartSubmit() => Source.StartActivity(Submit, ActivityKind.Server);

    /// <summary>Starts one stage under whatever activity is current.</summary>
    /// <remarks>
    /// Parentage is ambient rather than passed: <see cref="Activity.Current"/>
    /// is restored when a stage is disposed, so a stage started inside the
    /// submission is its child without the call sites threading a handle. That
    /// is also why every stage here is scoped by <c>using</c> — an undisposed
    /// stage becomes the parent of the next one and the trace reports a chain
    /// where the code has a sequence.
    /// </remarks>
    public static Activity? StartStage(string name) =>
        Source.StartActivity(name, ActivityKind.Internal, default(ActivityContext));

    /// <summary>Marks the submission as stopped by a §7 rejection code.</summary>
    public static void Rejected(this Activity? activity, string code) =>
        activity?.SetStatus(ActivityStatusCode.Error, code);
}
