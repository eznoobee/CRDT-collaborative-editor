using System.Text;
using Crdt.Core;

namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// Builds reproducible random scenarios from a seed.
/// </summary>
/// <remarks>
/// <para>
/// Every scenario is a function of its seed alone (PROJECT_SPEC.md §5), so a
/// failure is replayed by rerunning with the seed printed in the failure message.
/// </para>
/// <para>
/// Generation drives real replicas, so that every index it emits is in range for
/// the replica that will execute it. Choosing indices from a shared counter does
/// not work: replicas diverge between deliveries, so "the document is nine
/// characters long" is not true of all of them at once. The emitted
/// <see cref="Scenario"/> remains pure data and is replayed against fresh
/// replicas by <see cref="SimulationRunner"/>.
/// </para>
/// <para>
/// Scenarios mix two things deliberately. Random single edits exercise the
/// general invariants; structured <em>run sessions</em> — several replicas each
/// typing a run at the same position before any delivery — are what invariant 8
/// is about, and would essentially never arise from uniform random edits.
/// </para>
/// </remarks>
public static class ScenarioGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>Generates one scenario.</summary>
    public static Scenario Generate(int seed)
    {
        var rng = new Random(seed);
        var replicaCount = rng.Next(2, 5);
        var ids = Enumerable.Range(1, replicaCount).Select(ReplicaIds.Numbered).ToArray();
        var live = ids.Select(id => new Replica(id)).ToArray();

        var steps = new List<ScenarioStep>();
        var sessions = new List<RunSession>();

        Operation? Emit(ScenarioStep step)
        {
            steps.Add(step);
            return SimulationRunner.ApplyStep(live, step);
        }

        // A short shared prefix so concurrent edits have somewhere to land other
        // than an empty document.
        var prefixLength = rng.Next(0, 4);
        for (var i = 0; i < prefixLength; i++)
        {
            Emit(new InsertStep(0, i, Letter(rng)));
        }

        if (prefixLength > 0)
        {
            Emit(new SyncStep());
        }

        var rounds = rng.Next(1, 4);
        for (var round = 0; round < rounds; round++)
        {
            var choice = rng.Next(100);
            if (choice < 45)
            {
                sessions.Add(AppendRunSession(rng, live, steps, Emit, replicaCount));
            }
            else if (choice < 75 && replicaCount >= 3)
            {
                var nested = AppendLayeredSession(rng, live, steps, Emit, replicaCount);
                if (nested is not null)
                {
                    sessions.Add(nested);
                }
            }
            else
            {
                AppendRandomEdits(rng, live, Emit, replicaCount);
            }

            // Partial delivery some of the time, so replicas diverge before the
            // final sync rather than every round being trivially causal.
            if (rng.Next(100) < 40 && replicaCount > 1)
            {
                var from = rng.Next(replicaCount);
                var to = rng.Next(replicaCount);
                if (from != to)
                {
                    Emit(new DeliverStep(from, to));
                }
            }

            Emit(new SyncStep());
        }

        Emit(new SyncStep());
        return new Scenario(seed, ids, steps, sessions);
    }

    /// <summary>
    /// Adds several replicas each typing a run at the same position, with no
    /// delivery in between, so the runs are mutually concurrent.
    /// </summary>
    private static RunSession AppendRunSession(
        Random rng,
        Replica[] live,
        List<ScenarioStep> steps,
        Func<ScenarioStep, Operation?> emit,
        int replicaCount)
    {
        var concurrency = Math.Min(replicaCount, rng.Next(2, 4));
        var participants = Enumerable.Range(0, replicaCount)
            .OrderBy(_ => rng.Next())
            .Take(concurrency)
            .ToArray();

        // One position, valid on every participant. They have not exchanged
        // anything since the last sync, so their lengths agree, but taking the
        // minimum keeps this correct if that ever stops being true.
        var position = rng.Next(0, participants.Min(r => live[r].Values.Count) + 1);

        var runs = new List<Run>(concurrency);
        foreach (var replica in participants)
        {
            var length = rng.Next(2, 5);
            var direction = rng.Next(2) == 0 ? RunDirection.Forward : RunDirection.Backward;
            var text = new StringBuilder(length);
            var indices = new List<int>(length);

            for (var i = 0; i < length; i++)
            {
                var value = Letter(rng);
                indices.Add(steps.Count);
                if (direction == RunDirection.Forward)
                {
                    // Each character after the previous one.
                    emit(new InsertStep(replica, position + i, value));
                    text.Append(value.ToString());
                }
                else
                {
                    // Each character before the previous one, at a fixed
                    // position, so they all share one anchor.
                    emit(new InsertStep(replica, position, value));
                    text.Insert(0, value.ToString());
                }
            }

            runs.Add(new Run(replica, text.ToString(), direction, indices));
        }

        return new RunSession(runs);
    }

    /// <summary>
    /// The arXiv Theorem 5 counterexample shape (Fig. 7): two rounds of
    /// concurrency separated by a partial delivery, with the interleaving pairs
    /// formed as backward runs that span both rounds.
    /// </summary>
    /// <remarks>
    /// This is the only shape in which the Lemma 5 exception can arise. A single
    /// round of concurrent runs cannot produce it however many replicas take
    /// part, and nor can a layered execution whose second round is typed
    /// forwards — in the paper the pairs that interleave are <c>de</c> and
    /// <c>fg</c>, each typed right to left across two rounds.
    /// </remarks>
    private static RunSession? AppendLayeredSession(
        Random rng,
        Replica[] live,
        List<ScenarioStep> steps,
        Func<ScenarioStep, Operation?> emit,
        int replicaCount)
    {
        var picks = Enumerable.Range(0, replicaCount)
            .OrderBy(_ => rng.Next())
            .Take(3)
            .ToArray();

        var (p, q, r) = (picks[0], picks[1], picks[2]);
        var position = rng.Next(0, live.Min(x => x.Values.Count) + 1);

        // Round one: q and r insert concurrently at the same place.
        emit(new InsertStep(q, position, Letter(rng)));
        emit(new InsertStep(r, position, Letter(rng)));

        // p learns of r's character but not q's, so p and r now differ.
        emit(new DeliverStep(r, p));

        // Round two: p and r each insert one character, concurrently, into
        // those different views.
        var stepE = steps.Count;
        var opE = emit(new InsertStep(p, position, Letter(rng)));
        var stepG = steps.Count;
        var opG = emit(new InsertStep(r, position, Letter(rng)));

        if (opE is null || opG is null)
        {
            return null;
        }

        // Both now learn of q's character.
        emit(new DeliverStep(q, p));
        emit(new DeliverStep(q, r));

        // Round three: each prepends a character immediately before the one it
        // added in round two, making a backward run that spans both rounds.
        var stepD = steps.Count;
        var textD = Letter(rng);
        emit(new InsertStep(p, IndexOf(live[p], opE.Id), textD));

        var stepF = steps.Count;
        var textF = Letter(rng);
        emit(new InsertStep(r, IndexOf(live[r], opG.Id), textF));

        emit(new SyncStep());

        return new RunSession(
        [
            new Run(p, $"{textD}{opE.Id.Seq}", RunDirection.Backward, [stepD, stepE]),
            new Run(r, $"{textF}{opG.Id.Seq}", RunDirection.Backward, [stepF, stepG]),
        ]);
    }

    private static int IndexOf(Replica replica, ElementId id)
    {
        var visible = replica.VisibleIds;
        for (var i = 0; i < visible.Count; i++)
        {
            if (visible[i].Equals(id))
            {
                return i;
            }
        }

        return visible.Count;
    }

    private static void AppendRandomEdits(
        Random rng, Replica[] live, Func<ScenarioStep, Operation?> emit, int replicaCount)
    {
        var count = rng.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            var replica = rng.Next(replicaCount);
            var visible = live[replica].Values.Count;

            if (visible > 0 && rng.Next(100) < 25)
            {
                emit(new DeleteStep(replica, rng.Next(visible)));
            }
            else
            {
                emit(new InsertStep(replica, rng.Next(visible + 1), Letter(rng)));
            }
        }
    }

    private static Rune Letter(Random rng) => new(Alphabet[rng.Next(Alphabet.Length)]);
}

/// <summary>Deterministic replica ids for tests.</summary>
public static class ReplicaIds
{
    /// <summary>
    /// The id whose last byte is <paramref name="n"/>, so that ascending
    /// <paramref name="n"/> gives ascending replica ids under the §5 byte order.
    /// </summary>
    public static ReplicaId Numbered(int n)
    {
        Span<byte> bytes = stackalloc byte[ReplicaId.Size];
        bytes[^1] = checked((byte)n);
        return new ReplicaId(bytes);
    }
}
