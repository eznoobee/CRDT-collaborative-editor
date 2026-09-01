namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// Reduces a failing scenario to a smaller one that still fails.
/// </summary>
/// <remarks>
/// PROJECT_SPEC.md §5 requires shrinking, and the reason is practical: a
/// 40-step trace across four replicas says almost nothing, while the same bug
/// shown in four steps across two replicas usually says everything. This is
/// greedy delta debugging over the step list — remove a step, keep the removal
/// if the property still fails, repeat until nothing more can go.
/// </remarks>
public static class ScenarioShrinker
{
    private const int MaxPasses = 12;

    /// <summary>
    /// Returns the smallest scenario reachable by deleting steps that still
    /// satisfies <paramref name="stillFails"/>.
    /// </summary>
    public static Scenario Shrink(Scenario failing, Func<Scenario, bool> stillFails)
    {
        var current = failing;

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var shrunk = false;

            // Largest chunks first: removing a block is worth more than
            // removing a step, and converges much faster on long traces.
            for (var chunk = Math.Max(1, current.Steps.Count / 2); chunk >= 1; chunk /= 2)
            {
                for (var start = 0; start + chunk <= current.Steps.Count; start++)
                {
                    var candidate = WithoutRange(current, start, chunk);
                    if (candidate.Steps.Count == current.Steps.Count)
                    {
                        continue;
                    }

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
