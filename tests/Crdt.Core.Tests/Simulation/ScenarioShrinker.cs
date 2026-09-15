namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// Reduces a failing scenario to a smaller one that still fails.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §5 requires shrinking, and the reason is practical: a
/// 40-step trace across four replicas says almost nothing, while the same bug
/// shown in four steps across two replicas usually says everything. This is
/// greedy delta debugging over the step list — remove a step, keep the removal
/// if the property still fails, repeat until nothing more can go.
/// </para>
/// <para>
/// Since Phase 2.5 scenarios have a scale dimension (<see cref="ScenarioScale"/>)
/// and can run to thousands of steps, shrinking happens in two phases and
/// against a budget.
/// </para>
/// <para>
/// <b>Size first, independently of shape.</b> Truncating to a prefix costs
/// O(log n) replays and takes a two-thousand-step scenario to a few dozen
/// without ever asking which step mattered. Delta debugging alone would need
/// O(n log n) replays of an O(n²) simulation to do the same, which on a large
/// scenario means a shrink that never finishes — and a shrinker that hangs turns
/// a reproducible failure into an unreadable one.
/// </para>
/// <para>
/// <b>Then shape, against a replay budget.</b> The budget exists for the same
/// reason: a partly-shrunk scenario reported now beats a perfectly-shrunk one
/// that never arrives.
/// </para>
/// </remarks>
public static class ScenarioShrinker
{
    private const int MaxPasses = 12;

    /// <summary>
    /// Replays the delta-debugging phase may spend. Reached only on scenarios
    /// the size phase could not cut down, which is itself worth knowing.
    /// </summary>
    private const int ReplayBudget = 2_000;

    /// <summary>
    /// Returns the smallest scenario reachable by deleting steps that still
    /// satisfies <paramref name="stillFails"/>.
    /// </summary>
    public static Scenario Shrink(Scenario failing, Func<Scenario, bool> stillFails)
    {
        var current = ShrinkSize(failing, stillFails);
        var budget = ReplayBudget;

        for (var pass = 0; pass < MaxPasses && budget > 0; pass++)
        {
            var shrunk = false;

            // Largest chunks first: removing a block is worth more than
            // removing a step, and converges much faster on long traces.
            for (var chunk = Math.Max(1, current.Steps.Count / 2); chunk >= 1 && budget > 0; chunk /= 2)
            {
                for (var start = 0; start + chunk <= current.Steps.Count && budget > 0; start++)
                {
                    var candidate = WithoutRange(current, start, chunk);
                    if (candidate.Steps.Count == current.Steps.Count)
                    {
                        continue;
                    }

                    budget--;
                    if (SafelyStillFails(candidate, stillFails))
                    {
                        current = candidate;
                        shrunk = true;
                        start--;
                    }
                }
            }

            if (!shrunk)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// Cuts magnitude without asking which steps matter: keep the first half,
    /// then the first quarter, and so on for as long as the property still fails.
    /// </summary>
    /// <remarks>
    /// A failure that survives truncation was never about the tail, and this
    /// finds that out in a handful of replays. A failure that does not survive
    /// truncation is left at full size for the shape phase, which is the correct
    /// answer rather than a giving-up: the tail is load-bearing.
    /// </remarks>
    private static Scenario ShrinkSize(Scenario failing, Func<Scenario, bool> stillFails)
    {
        var current = failing;

        for (var length = current.Steps.Count / 2; length >= 1; length /= 2)
        {
            var candidate = FirstSteps(current, length);
            if (candidate.Steps.Count >= current.Steps.Count)
            {
                break;
            }

            if (!SafelyStillFails(candidate, stillFails))
            {
                break;
            }

            current = candidate;
            length = current.Steps.Count;
        }

        return current;
    }

    private static Scenario FirstSteps(Scenario scenario, int length)
    {
        var steps = scenario.Steps.Take(length).ToList();

        // As in WithoutRange: a scenario that never synchronises proves nothing
        // about convergence.
        if (steps.Count == 0 || steps[^1] is not SyncStep)
        {
            steps.Add(new SyncStep());
        }

        var survivingReplicas = steps.OfType<InsertStep>().Select(s => s.Replica).ToHashSet();
        var sessions = scenario.Sessions
            .Where(s => s.Runs.All(r =>
                survivingReplicas.Contains(r.Replica) && r.StepIndices.All(i => i < length)))
            .ToList();

        return scenario with { Steps = steps, Sessions = sessions };
    }

    private static Scenario WithoutRange(Scenario scenario, int start, int count)
    {
        var steps = scenario.Steps.Take(start)
            .Concat(scenario.Steps.Skip(start + count))
            .ToList();

        // A scenario that never synchronises proves nothing about convergence,
        // so the terminating sync is never shrunk away.
        if (steps.Count == 0 || steps[^1] is not SyncStep)
        {
            steps.Add(new SyncStep());
        }

        // Sessions whose steps are gone are no longer claims about this trace.
        var survivingReplicas = steps.OfType<InsertStep>().Select(s => s.Replica).ToHashSet();
        var sessions = scenario.Sessions
            .Where(s => s.Runs.All(r => survivingReplicas.Contains(r.Replica)))
            .ToList();

        return scenario with { Steps = steps, Sessions = sessions };
    }

    /// <summary>
    /// A candidate that cannot be replayed at all is not a smaller reproduction.
    /// </summary>
    /// <remarks>
    /// Deleting an insert shifts every later index, so a candidate can address a
    /// position that no longer exists. That is a malformed trace rather than a
    /// bug, and accepting it would replace a real failure with a bogus one.
    /// </remarks>
    private static bool SafelyStillFails(Scenario candidate, Func<Scenario, bool> stillFails)
    {
        try
        {
            return stillFails(candidate);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
    }
}
