using System.Globalization;

namespace Crdt.Core.Tests.Simulation;

/// <summary>
/// How large the scale cases are allowed to get, so mutation runs can shrink
/// them the way <see cref="PropertyRunner.DefaultCases"/> shrinks case counts.
/// </summary>
/// <remarks>
/// <para>
/// Stryker runs the whole suite once per mutant against a timeout, and a mutant
/// that merely makes the suite slow is then recorded as detected. Left
/// unbounded, the scale cases turned sixteen kills into timeouts and five
/// survivors into timeouts — the score went up while nothing new was actually
/// caught, and the counts became a function of how fast the machine was.
/// </para>
/// <para>
/// That matters because §13.7's ratchet compares the score exactly, and an exact
/// comparison is only sound while the score is deterministic. Bounding the size
/// under mutation keeps it so. Nothing is skipped: the cases still run, at a
/// size that does not dominate the clock.
/// </para>
/// </remarks>
public static class ScaleBudget
{
    /// <summary>Largest document a scale case may build. 150,000 unless overridden.</summary>
    public static int MaxElements { get; } =
        int.TryParse(
            Environment.GetEnvironmentVariable("CRDT_SCALE_ELEMENTS"),
            CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : 150_000;

    /// <summary>True when running under a reduced budget, so loops can shorten too.</summary>
    public static bool IsReduced => MaxElements < 150_000;
}
