using System.Text;
using Crdt.Core;

namespace Crdt.Core.Tests.Simulation;

/// <summary>Direction in which a run of characters was typed.</summary>
/// <remarks>
/// A forward run is ordinary typing: each character is inserted after the
/// previous one. A backward run inserts each character <em>before</em> the
/// previous one, at the same position — caret-left editing, not a right-to-left
/// script (PROJECT_SPEC.md §5).
/// </remarks>
public enum RunDirection
{
    Forward,
    Backward,
}

/// <summary>One replica's contribution to a concurrent editing session.</summary>
/// <param name="StepIndices">
/// Indices into <see cref="Scenario.Steps"/> of the inserts forming this run, so
/// the run can be matched to its elements by identity. Matching by character
/// would be wrong: runs repeat letters, and the whole question is which element
/// landed where.
/// </param>
public sealed record Run(
    int Replica,
    string Text,
    RunDirection Direction,
    IReadOnlyList<int> StepIndices);

/// <summary>
/// A set of runs typed concurrently at the same position by different replicas.
/// </summary>
/// <remarks>
/// Recorded by the generator so the invariant 8 assertions can scope themselves.
/// Whether run-level contiguity may be demanded depends on how many replicas
/// took part, which is knowable only here and not from the final text.
/// </remarks>
public sealed record RunSession(IReadOnlyList<Run> Runs)
{
    /// <summary>Number of replicas inserting concurrently at this position.</summary>
    public int Concurrency => Runs.Count;
}

/// <summary>One step of a scenario.</summary>
public abstract record ScenarioStep;

/// <summary>Insert a code point at a visible index on one replica.</summary>
public sealed record InsertStep(int Replica, int Index, Rune Value) : ScenarioStep;

/// <summary>Delete the element at a visible index on one replica.</summary>
public sealed record DeleteStep(int Replica, int Index) : ScenarioStep;

/// <summary>Deliver everything <paramref name="From"/> knows to <paramref name="To"/>.</summary>
public sealed record DeliverStep(int From, int To) : ScenarioStep;

/// <summary>Deliver in every direction until all replicas agree.</summary>
public sealed record SyncStep : ScenarioStep;

/// <summary>
/// A reproducible execution: replicas, a schedule, and the run sessions the
/// schedule contains.
/// </summary>
/// <remarks>
/// The seed is carried so that any failure can be replayed exactly.
/// PROJECT_SPEC.md §5 calls this non-negotiable: a CRDT bug you cannot
/// reproduce is a CRDT bug you cannot fix.
/// </remarks>
public sealed record Scenario(
    int Seed,
    IReadOnlyList<ReplicaId> Replicas,
    IReadOnlyList<ScenarioStep> Steps,
    IReadOnlyList<RunSession> Sessions)
{
    /// <summary>Renders the scenario as replayable pseudo-code for failure output.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("seed ").Append(Seed).Append(", ")
          .Append(Replicas.Count).AppendLine(" replicas");
        for (var i = 0; i < Replicas.Count; i++)
        {
            sb.Append("  r").Append(i).Append(" = ").AppendLine(Replicas[i].ToString());
        }

        foreach (var step in Steps)
        {
            sb.Append("  ").AppendLine(step switch
            {
                InsertStep s => $"r{s.Replica}.Insert({s.Index}, '{s.Value}')",
                DeleteStep s => $"r{s.Replica}.Delete({s.Index})",
                DeliverStep s => $"deliver r{s.From} -> r{s.To}",
                SyncStep => "sync",
                _ => step.ToString() ?? "?",
            });
        }

        foreach (var session in Sessions)
        {
            sb.Append("  session concurrency=").Append(session.Concurrency).Append(':');
            foreach (var run in session.Runs)
            {
                sb.Append(" r").Append(run.Replica).Append('=').Append('"').Append(run.Text)
                  .Append('"').Append('(').Append(run.Direction).Append(')');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
