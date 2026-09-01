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
/// Scenarios mix two things deliberately. Random single edits exercise the
/// general invariants; structured <em>run sessions</em> — several replicas each
/// typing a run at the same position before any delivery — are what invariant 8
/// is about, and would essentially never arise from uniform random edits.
/// </para>
/// </remarks>
public sealed class ScenarioGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>Generates one scenario.</summary>
    public static Scenario Generate(int seed)
    {
        var rng = new Random(seed);
        var replicaCount = rng.Next(2, 5);
        var replicas = Enumerable.Range(1, replicaCount)
            .Select(i => ReplicaIds.Numbered(i))
            .ToArray();

        var steps = new List<ScenarioStep>();
        var sessions = new List<RunSession>();

        // A short shared prefix so concurrent edits have somewhere to land other
        // than an empty document.
        var prefixLength = rng.Next(0, 4);
        for (var i = 0; i < prefixLength; i++)
        {
            steps.Add(new InsertStep(0, i, Letter(rng)));
        }

        if (prefixLength > 0)
        {
            steps.Add(new SyncStep());
        }

        var visible = prefixLength;
        var rounds = rng.Next(1, 4);

        for (var round = 0; round < rounds; round++)
        {
            if (rng.Next(100) < 65)
            {
                var session = AppendRunSession(rng, steps, replicaCount, visible);
                sessions.Add(session);
                visible += session.Runs.Sum(r => r.Text.Length);
            }
            else
            {
                visible = AppendRandomEdits(rng, steps, replicaCount, visible);
            }

            // Partial delivery some of the time, so replicas diverge before the
            // final sync rather than every round being trivially causal.
            if (rng.Next(100) < 40 && replicaCount > 1)
            {
                var from = rng.Next(replicaCount);
                var to = rng.Next(replicaCount);
                if (from != to)
                {
                    steps.Add(new DeliverStep(from, to));
                }
            }

            steps.Add(new SyncStep());
        }

        steps.Add(new SyncStep());
        return new Scenario(seed, replicas, steps, sessions);
    }

    /// <summary>
    /// Adds several replicas each typing a run at the same position, with no
    /// delivery in between, so the runs are mutually concurrent.
    /// </summary>
    private static RunSession AppendRunSession(
        Random rng, List<ScenarioStep> steps, int replicaCount, int visible)
    {
        var concurrency = Math.Min(replicaCount, rng.Next(2, 4));
        var position = rng.Next(0, visible + 1);
        var participants = Enumerable.Range(0, replicaCount)
            .OrderBy(_ => rng.Next())
            .Take(concurrency)
            .ToArray();

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
                    steps.Add(new InsertStep(replica, position + i, value));
                    text.Append(value.ToString());
                }
                else
                {
                    // Each character before the previous one, at a fixed
                    // position, so they all share one anchor.
                    steps.Add(new InsertStep(replica, position, value));
                    text.Insert(0, value.ToString());
                }
            }

            runs.Add(new Run(replica, text.ToString(), direction, indices));
        }

        return new RunSession(runs);
    }

    private static int AppendRandomEdits(
        Random rng, List<ScenarioStep> steps, int replicaCount, int visible)
    {
        var count = rng.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            var replica = rng.Next(replicaCount);
            if (visible > 0 && rng.Next(100) < 25)
            {
                steps.Add(new DeleteStep(replica, rng.Next(visible)));
                visible--;
            }
            else
            {
                steps.Add(new InsertStep(replica, rng.Next(visible + 1), Letter(rng)));
                visible++;
            }
        }

        return visible;
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
