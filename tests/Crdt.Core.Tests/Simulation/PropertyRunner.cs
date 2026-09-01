using Xunit.Sdk;

namespace Crdt.Core.Tests.Simulation;

/// <summary>Counts how often an observed-but-not-enforced property held.</summary>
/// <remarks>
/// PROJECT_SPEC.md §5 requires the three-replica boundary for backward
/// contiguity to be measured rather than assumed. An observation is evaluated on
/// every applicable case and reported, but never fails the run: the point is to
/// learn whether the boundary is drawn in the right place, and a boundary that
/// is never approached teaches nothing.
/// </remarks>
public sealed class Observation(string name)
{
    public string Name { get; } = name;

    public int Held { get; private set; }

    public int Violated { get; private set; }

    public int Total => Held + Violated;

    public void Record(bool held)
    {
        if (held)
        {
            Held++;
        }
        else
        {
            Violated++;
        }
    }

    public string Report() => Total == 0
        ? $"{Name}: no applicable cases"
        : $"{Name}: held {Held}/{Total} ({100.0 * Held / Total:F2}%), violated {Violated}";
}

/// <summary>Runs a property over many generated scenarios, shrinking on failure.</summary>
public static class PropertyRunner
{
    /// <summary>Cases per property. PROJECT_SPEC.md §11, Phase 1 done-when.</summary>
    public const int DefaultCases = 10_000;

    /// <summary>
    /// Checks <paramref name="property"/> on each generated scenario. The first
    /// failure is shrunk and reported with the seed that reproduces it.
    /// </summary>
    public static void Check(
        string name,
        Action<Scenario, SimulationResult> property,
        int cases = DefaultCases,
        int firstSeed = 0)
    {
        for (var seed = firstSeed; seed < firstSeed + cases; seed++)
        {
            var scenario = ScenarioGenerator.Generate(seed);

            try
            {
                property(scenario, SimulationRunner.Run(scenario));
            }
            catch (NotImplementedException)
            {
                // The implementation does not exist yet. Shrinking would reduce
                // every scenario to nothing and say nothing, so report straight
                // away — this is the expected state between tasks 1.2 and 1.4.
                throw;
            }
            catch (Exception ex) when (ex is XunitException or InvalidOperationException)
            {
                var minimal = ScenarioShrinker.Shrink(
                    scenario,
                    candidate =>
                    {
                        try
                        {
                            property(candidate, SimulationRunner.Run(candidate));
                            return false;
                        }
                        catch (NotImplementedException)
                        {
                            throw;
                        }
                        catch (Exception inner) when (inner is XunitException or InvalidOperationException)
                        {
                            return true;
                        }
                    });

                throw new XunitException(
                    $"""
                     Property '{name}' failed.

                     Reproduce with seed {seed}:
                         PropertyRunner.Check("{name}", ..., cases: 1, firstSeed: {seed});

                     Minimised scenario ({minimal.Steps.Count} steps, was {scenario.Steps.Count}):
                     {minimal.Describe()}
                     Original failure: {ex.Message}
                     """);
            }
        }
    }
}
