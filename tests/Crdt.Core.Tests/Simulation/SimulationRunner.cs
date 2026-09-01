using Crdt.Core;

namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// What a replica saw around an insert at the moment it made it.
/// </summary>
/// <remarks>
/// Captured during replay because it cannot be recovered afterwards: invariant 5
/// is about where the author put a character relative to its neighbours, and
/// once other replicas' edits merge in, the neighbours by index are no longer
/// the neighbours by intent.
/// </remarks>
public sealed record Intention(ElementId Inserted, ElementId? Left, ElementId? Right);

/// <summary>The outcome of replaying a scenario.</summary>
public sealed record SimulationResult(
    IReadOnlyList<Replica> Replicas,
    IReadOnlyList<Operation> AllOperations,
    IReadOnlyList<Intention> Intentions,
    IReadOnlyList<ElementId> DeletedIds,
    IReadOnlyList<Operation?> OperationByStepIndex)
{
    /// <summary>Final visible text of each replica, indexed as in the scenario.</summary>
    public IReadOnlyList<string> Texts => [.. Replicas.Select(r => r.Text)];

    /// <summary>True when every replica shows the same text.</summary>
    public bool Converged => Texts.Distinct(StringComparer.Ordinal).Count() <= 1;
}

/// <summary>Replays a <see cref="Scenario"/> against real replicas.</summary>
public static class SimulationRunner
{
    /// <summary>Replays the whole scenario.</summary>
    public static SimulationResult Run(Scenario scenario)
    {
        var replicas = scenario.Replicas.Select(id => new Replica(id)).ToArray();
        var all = new List<Operation>();
        var intentions = new List<Intention>();
        var deleted = new List<ElementId>();
        var byStep = new Operation?[scenario.Steps.Count];

        for (var stepIndex = 0; stepIndex < scenario.Steps.Count; stepIndex++)
        {
            var step = scenario.Steps[stepIndex];
            switch (step)
            {
                case InsertStep s:
                    {
                        var replica = replicas[s.Replica];
                        var before = replica.VisibleIds;
                        var left = s.Index > 0 ? before[s.Index - 1] : (ElementId?)null;
                        var right = s.Index < before.Count ? before[s.Index] : (ElementId?)null;

                        var op = replica.Insert(s.Index, s.Value);
                        all.Add(op);
                        byStep[stepIndex] = op;
                        intentions.Add(new Intention(op.Id, left, right));
                        break;
                    }

                case DeleteStep s:
                    {
                        var op = replicas[s.Replica].Delete(s.Index);
                        all.Add(op);
                        byStep[stepIndex] = op;
                        deleted.Add(op.Target);
                        break;
                    }

                case DeliverStep s:
                    Deliver(replicas[s.From], replicas[s.To]);
                    break;
                case SyncStep:
                    SyncAll(replicas);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown step {step}.");
            }
        }

        return new SimulationResult(replicas, all, intentions, deleted, byStep);
    }

    /// <summary>Delivers everything <paramref name="from"/> knows to <paramref name="to"/>.</summary>
    public static void Deliver(Replica from, Replica to)
    {
        foreach (var op in from.OperationsSince(to.VersionVector))
        {
            to.Apply(op);
        }
    }

    /// <summary>Delivers in every direction until quiescent.</summary>
    public static void SyncAll(IReadOnlyList<Replica> replicas)
    {
        // Two passes over every ordered pair is enough for a fixed point: after
        // the first pass every replica has seen every operation that existed at
        // entry, and delivery generates none.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var from in replicas)
            {
                foreach (var to in replicas)
                {
                    if (!ReferenceEquals(from, to))
                    {
                        Deliver(from, to);
                    }
                }
            }
        }
    }
}
