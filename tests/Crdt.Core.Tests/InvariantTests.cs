using Crdt.Core.Tests.Simulation;

namespace Crdt.Core.Tests;

/// <summary>
/// The acceptance criteria for <c>Crdt.Core</c>: PROJECT_SPEC.md §5's eight
/// invariants, each over <see cref="PropertyRunner.DefaultCases"/> generated
/// scenarios.
/// </summary>
/// <remarks>
/// Written before the implementation exists, per §12. Until task 1.4 lands they
/// all fail with <see cref="NotImplementedException"/>, which is the intended
/// red state and not a defect in the tests.
/// </remarks>
public sealed class InvariantTests(ITestOutputHelper output)
{
    [Fact]
    public void Invariant_1_convergence()
    {
        PropertyRunner.Check("convergence", (_, result) =>
        {
            Assert.True(
                result.Converged,
                "replicas that have seen the same operations must produce identical text, got: "
                + string.Join(" | ", result.Texts.Select(t => $"\"{t}\"")));
        });
    }

    [Fact]
    public void Invariant_2_idempotency()
    {
        PropertyRunner.Check("idempotency", (_, result) =>
        {
            var once = Rebuild(result.AllOperations, 200);
            var twice = Rebuild(result.AllOperations.Concat(result.AllOperations), 201);

            Assert.Equal(once.Text, twice.Text);
        });
    }

    [Fact]
    public void Invariant_3_commutativity()
    {
        PropertyRunner.Check("commutativity", (scenario, result) =>
        {
            // Two different delivery orders of the same operation set. Causal
            // dependencies are honoured by buffering, not by the order here.
            var forwards = Rebuild(result.AllOperations, 210);
            var backwards = Rebuild(result.AllOperations.Reverse(), 211);
            var shuffled = Rebuild(Shuffle(result.AllOperations, scenario.Seed), 212);

            Assert.Equal(forwards.Text, backwards.Text);
            Assert.Equal(forwards.Text, shuffled.Text);
        });
    }

    [Fact]
    public void Invariant_4_causal_readiness()
    {
        PropertyRunner.Check("causal readiness", (scenario, result) =>
        {
            // Delivered in an arbitrary order, every operation must eventually
            // apply: nothing dropped, nothing stuck. A replica still holding
            // buffered operations once it has seen them all has either lost a
            // dependency or applied something out of order.
            var observer = Rebuild(Shuffle(result.AllOperations, scenario.Seed + 7), 220);

            Assert.Equal(0, observer.PendingCount);
            Assert.Equal(result.Replicas[0].Text, observer.Text);

            foreach (var replica in result.Replicas)
            {
                Assert.Equal(0, replica.PendingCount);
            }
        });
    }

    [Fact]
    public void Invariant_5_intention_preservation()
    {
        PropertyRunner.Check("intention preservation", (_, result) =>
        {
            // Full order, including tombstones: a character's recorded right
            // origin may itself have been deleted since, and skipping those
            // pairs would quietly drop most of the checks in any scenario that
            // deletes anything.
            var order = result.Replicas[0].AllIds;
            var position = order.Select((id, i) => (id, i))
                .ToDictionary(p => p.id, p => p.i);

            foreach (var intention in result.Intentions)
            {
                if (!position.TryGetValue(intention.Inserted, out var inserted))
                {
                    continue; // deleted since
                }

                if (intention.Left is { } left && position.TryGetValue(left, out var l))
                {
                    Assert.True(
                        l < inserted,
                        "a character inserted after X must stay after X (§5 invariant 5)");
                }

                if (intention.Right is { } right && position.TryGetValue(right, out var r))
                {
                    Assert.True(
                        inserted < r,
                        "a character inserted before Y must stay before Y (§5 invariant 5)");
                }
            }
        });
    }

    [Fact]
    public void Invariant_6_no_resurrection()
    {
        PropertyRunner.Check("no resurrection", (_, result) =>
        {
            var deleted = result.DeletedIds.ToHashSet();

            foreach (var replica in result.Replicas)
            {
                foreach (var id in replica.VisibleIds)
                {
                    Assert.DoesNotContain(id, deleted);
                }
            }
        });
    }

    [Fact]
    public void Invariant_7_gc_safety()
    {
        PropertyRunner.Check("gc safety", (_, result) =>
        {
            // After a full sync every replica has seen everything, so the
            // elementwise minimum of the version vectors is the causal
            // stability frontier and every tombstone below it is collectable.
            var frontier = StableFrontier(result);

            var before = result.Replicas[0].Text;
            result.Replicas[0].Collect(frontier);

            Assert.Equal(before, result.Replicas[0].Text);

            // A subsequent legal operation must still converge across a
            // collected and an uncollected replica.
            if (result.Replicas.Count > 1)
            {
                var op = result.Replicas[1].Insert(result.Replicas[1].Values.Count, new System.Text.Rune('z'));
                result.Replicas[0].Apply(op);

                Assert.Equal(result.Replicas[1].Text, result.Replicas[0].Text);
            }
        });
    }

    [Fact]
    public void Invariant_8_maximal_non_interleaving()
    {
        var backwardContiguity = new Observation("backward run contiguity");

        PropertyRunner.Check("maximal non-interleaving", (scenario, result) =>
        {
            // Level 1: Definition 4 itself, on every execution. Exact, and the
            // part that catches a misreading the conformance harness could not.
            var analysis = MaximalNonInterleaving.Analyse(result);

            Assert.Empty(analysis.ForwardViolations());
            Assert.Empty(analysis.BackwardViolations());
            Assert.Empty(analysis.SameOriginViolations());

            // Level 2: run-level contiguity, scoped to what FugueMax is
            // permitted to satisfy (§5).
            var visible = result.Replicas[0].VisibleIds;
            var position = visible.Select((id, i) => (id, i))
                .ToDictionary(p => p.id, p => p.i);

            foreach (var session in scenario.Sessions)
            {
                // Contiguity is judged only against the other runs of the same
                // session. Non-interleaving constrains concurrent insertions; a
                // causally later edit typed inside a run is the author putting it
                // there, and demanding the run stay pristine forever would be
                // asserting something no algorithm should satisfy.
                var labelled = new List<(int Position, int Run)>();
                for (var runIndex = 0; runIndex < session.Runs.Count; runIndex++)
                {
                    foreach (var stepIndex in session.Runs[runIndex].StepIndices)
                    {
                        if (result.OperationByStepIndex[stepIndex] is { } op
                            && position.TryGetValue(op.Id, out var p))
                        {
                            labelled.Add((p, runIndex));
                        }
                    }
                }

                if (labelled.Count < 2)
                {
                    continue;
                }

                labelled.Sort((a, b) => a.Position.CompareTo(b.Position));

                for (var runIndex = 0; runIndex < session.Runs.Count; runIndex++)
                {
                    var occupied = labelled
                        .Select((entry, order) => (entry.Run, order))
                        .Where(x => x.Run == runIndex)
                        .Select(x => x.order)
                        .ToArray();

                    if (occupied.Length < 2)
                    {
                        continue;
                    }

                    var contiguous = occupied[^1] - occupied[0] == occupied.Length - 1;
                    var run = session.Runs[runIndex];

                    if (run.Direction == RunDirection.Forward)
                    {
                        // Lemma 3 gives this unconditionally, for any number of
                        // replicas.
                        Assert.True(
                            contiguous,
                            $"a forward run of {occupied.Length} characters at "
                            + $"concurrency {session.Concurrency} must not be interleaved with "
                            + "the runs it was concurrent with (§5 invariant 8a)");
                    }
                    else
                    {
                        // Observed, never enforced. There is no backward
                        // analogue of Lemma 3 at any replica count — Theorem 5
                        // forbids one — so the only sound statement about
                        // backward runs is Definition 4, checked above.
                        backwardContiguity.Record(contiguous);
                    }
                }
            }
        });

        output.WriteLine(backwardContiguity.Report());
        output.WriteLine(
            "Backward contiguity is measured, not required: arXiv Theorem 5 rules out any "
            + "unconditional backward guarantee, and violations here are the algorithm "
            + "behaving correctly. Definition 4 above is what is enforced.");
    }

    private static Replica Rebuild(IEnumerable<Operation> operations, int observerId)
    {
        var replica = new Replica(ReplicaIds.Numbered(observerId));
        foreach (var op in operations)
        {
            replica.Apply(op);
        }

        return replica;
    }

    private static IReadOnlyList<Operation> Shuffle(IReadOnlyList<Operation> operations, int seed)
    {
        var rng = new Random(seed);
        return [.. operations.OrderBy(_ => rng.Next())];
    }

    private static Dictionary<ReplicaId, ulong> StableFrontier(SimulationResult result)
    {
        var frontier = new Dictionary<ReplicaId, ulong>();
        foreach (var replica in result.Replicas)
        {
            foreach (var (id, seq) in replica.VersionVector)
            {
                frontier[id] = frontier.TryGetValue(id, out var existing)
                    ? Math.Min(existing, seq)
                    : seq;
            }
        }

        return frontier;
    }
}
