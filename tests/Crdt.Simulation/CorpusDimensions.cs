using System.Collections.Frozen;

namespace Crdt.Simulation;

/// <summary>
/// What §5 says a corpus has to reach, measured on the scenarios themselves.
/// </summary>
/// <remarks>
/// <para>
/// PROJECT_SPEC.md §9. A count of traces is not coverage — a thousand copies of
/// one shape satisfy any criterion phrased as a number — so the corpus is
/// characterised by its distribution over these dimensions, and a dimension at
/// zero fails the phase.
/// </para>
/// <para>
/// <b>Measured on the produced scenario, never on the generator's parameters.</b>
/// A histogram of the knobs is a histogram of intentions: a weight of 0.45 for
/// run sessions says nothing about how many sessions a downstream guard let
/// through, and the generator does drop them — <c>AppendLayeredSession</c>
/// returns null when the document is too small. Counting what came out is the
/// only way to learn that.
/// </para>
/// </remarks>
public static class CorpusDimensions
{
    /// <summary>Concurrent inserts at one position — §5's tie-break.</summary>
    public const string ConcurrentAtOnePosition = "concurrent-at-one-position";

    /// <summary>Runs of ≥2 typed concurrently by ≥2 replicas: the interleaving case.</summary>
    public const string InterleavingPressure = "interleaving-pressure";

    /// <summary>
    /// Concurrent inserts of a SINGLE character at one position.
    /// </summary>
    /// <remarks>
    /// The tie-break on its own, with no run to keep contiguous. Separated from
    /// <see cref="InterleavingPressure"/> because the two were found to be the
    /// same measurement: every scale sets <c>MinRunLength</c> to at least two,
    /// so every concurrent session already carried runs, and "interleaving
    /// pressure" was true whenever "concurrent at one position" was. A dimension
    /// that cannot vary independently of another is not measuring anything
    /// (§13.19).
    /// </remarks>
    public const string SingleCharacterConcurrency = "single-character-concurrency";

    /// <summary>A run typed right-to-left (§13.6's boundary).</summary>
    public const string BackwardRun = "backward-run";

    /// <summary>A delete of an element another replica inserted concurrently.</summary>
    public const string DeleteOfConcurrentInsert = "delete-of-concurrent-insert";

    /// <summary>Delivery that leaves a replica behind — §5's readiness path.</summary>
    public const string CausallyDelayedDelivery = "causally-delayed-delivery";

    /// <summary>Three or more replicas: TPDS Fig. 6's three-way tie-break.</summary>
    public const string ThreeOrMoreReplicas = "three-or-more-replicas";

    /// <summary>Every dimension §9 names, in a fixed order for reporting.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ConcurrentAtOnePosition,
        InterleavingPressure,
        SingleCharacterConcurrency,
        BackwardRun,
        DeleteOfConcurrentInsert,
        CausallyDelayedDelivery,
        ThreeOrMoreReplicas,
    ];

    /// <summary>Which dimensions this one scenario reaches.</summary>
    public static FrozenSet<string> Of(Scenario scenario)
    {
        var hit = new HashSet<string>(StringComparer.Ordinal);

        if (scenario.Replicas.Count >= 3)
        {
            hit.Add(ThreeOrMoreReplicas);
        }

        foreach (var session in scenario.Sessions)
        {
            if (session.Concurrency >= 2)
            {
                hit.Add(ConcurrentAtOnePosition);

                // Two replicas each contributing a run of at least two is the
                // shape that separates FugueMax from RGA. Single characters
                // inserted concurrently cannot: there is nothing to interleave.
                if (session.Runs.Count(run => run.Text.Length >= 2) >= 2)
                {
                    hit.Add(InterleavingPressure);
                }

                // Two replicas each contributing exactly one character: §5's
                // tie-break with nothing else in play. A corpus without this
                // never tests ElementId ordering in isolation — every failure
                // could be blamed on run handling instead.
                if (session.Runs.Count(run => run.Text.Length == 1) >= 2)
                {
                    hit.Add(SingleCharacterConcurrency);
                }
            }

            if (session.Runs.Any(run => run.Direction == RunDirection.Backward))
            {
                hit.Add(BackwardRun);
            }
        }

        if (DeletesAConcurrentInsert(scenario))
        {
            hit.Add(DeleteOfConcurrentInsert);
        }

        if (LeavesAReplicaBehind(scenario))
        {
            hit.Add(CausallyDelayedDelivery);
        }

        return hit.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Counts, over a whole corpus, how many scenarios reach each dimension.</summary>
    public static IReadOnlyDictionary<string, int> Count(IEnumerable<Scenario> corpus)
    {
        var counts = All.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
        foreach (var scenario in corpus)
        {
            foreach (var dimension in Of(scenario))
            {
                counts[dimension]++;
            }
        }

        return counts;
    }

    /// <summary>
    /// A delete issued by a replica that had not yet heard everyone's inserts.
    /// </summary>
    /// <remarks>
    /// Approximated from the schedule rather than from the tree: a delete
    /// counts when another replica inserted after the last point at which the
    /// deleting replica was synchronised. That is the condition under which the
    /// deleted element may be adjacent to an element it never saw, which is
    /// where <c>RightOrigin</c> handling is exercised rather than assumed.
    /// </remarks>
    private static bool DeletesAConcurrentInsert(Scenario scenario)
    {
        var insertedSinceSync = new HashSet<int>();

        foreach (var step in scenario.Steps)
        {
            switch (step)
            {
                case SyncStep:
                    insertedSinceSync.Clear();
                    break;

                case InsertStep insert:
                    insertedSinceSync.Add(insert.Replica);
                    break;

                case DeleteStep delete when insertedSinceSync.Any(r => r != delete.Replica):
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A one-way delivery that left at least one replica holding less than the
    /// sender — §5's causal readiness path, entered only when a replica hears
    /// about an operation whose predecessors it does not have.
    /// </summary>
    private static bool LeavesAReplicaBehind(Scenario scenario)
    {
        var pending = false;

        foreach (var step in scenario.Steps)
        {
            switch (step)
            {
                case SyncStep:
                    pending = false;
                    break;

                case InsertStep or DeleteStep:
                    pending = true;
                    break;

                // A one-way deliver while some replica has unshared work is the
                // case that matters: the receiver now holds operations the
                // others do not, so a later delivery can arrive out of order.
                case DeliverStep when pending && scenario.Replicas.Count > 2:
                    return true;
            }
        }

        return false;
    }
}
