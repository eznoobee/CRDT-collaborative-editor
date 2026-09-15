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
        var allOperations = new List<Operation>();
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

                        // Left origin is the previous VISIBLE element; right
                        // origin is the next element INCLUDING TOMBSTONES. That
                        // asymmetry is the paper's (Algorithm 1 lines 23-24, and
                        // arXiv §5.1), and taking both from the visible list
                        // silently redefines the property being checked.
                        var visible = replica.VisibleIds;
                        var all = replica.AllIds;
                        var left = s.Index > 0 ? visible[s.Index - 1] : (ElementId?)null;

                        var afterLeft = 0;
                        if (left is { } leftId)
                        {
                            for (var k = 0; k < all.Count; k++)
                            {
                                if (all[k].Equals(leftId))
                                {
                                    afterLeft = k + 1;
                                    break;
                                }
                            }
                        }

                        var right = afterLeft < all.Count ? all[afterLeft] : (ElementId?)null;

                        var op = replica.Insert(s.Index, s.Value);
                        allOperations.Add(op);
                        byStep[stepIndex] = op;
                        intentions.Add(new Intention(op.Id, left, right));
                        break;
                    }

                case DeleteStep s:
                    {
                        var op = replicas[s.Replica].Delete(s.Index);
                        allOperations.Add(op);
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

        return new SimulationResult(replicas, allOperations, intentions, deleted, byStep);
    }

    /// <summary>
    /// Applies one step to a live set of replicas.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="ScenarioGenerator"/>, which drives real replicas
    /// while generating so that every index it emits is in range for the replica
    /// that will execute it. Computing indices from a shared counter does not
    /// work: replicas diverge between deliveries, so "the document is nine
    /// characters long" is not true of all of them at once.
    /// </remarks>
    public static Operation? ApplyStep(IReadOnlyList<Replica> replicas, ScenarioStep step)
    {
        ArgumentNullException.ThrowIfNull(replicas);

        switch (step)
        {
            case InsertStep s:
                return replicas[s.Replica].Insert(s.Index, s.Value);
            case DeleteStep s:
                return replicas[s.Replica].Delete(s.Index);
            case DeliverStep s:
                Deliver(replicas[s.From], replicas[s.To]);
                return null;
            case SyncStep:
                SyncAll(replicas);
                return null;
            default:
                throw new InvalidOperationException($"Unknown step {step}.");
        }
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
