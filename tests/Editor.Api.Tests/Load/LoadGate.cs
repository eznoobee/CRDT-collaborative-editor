namespace Editor.Api.Tests.Load;

/// <summary>
/// Keeps §8's measurements out of the ordinary test run.
/// </summary>
/// <remarks>
/// <para>
/// These take minutes and need the box to themselves, which is the opposite of
/// what a suite run alongside a build wants — and a measurement sharing a
/// machine with a compiler is a measurement of the compiler.
/// </para><para>
/// Gated by an environment variable rather than a trait, so the same filter
/// that runs everything else keeps skipping them and <c>scripts/load.sh</c> is
/// the only thing that turns them on. The skip is <em>visible</em>: xunit
/// reports it, so "the load tests passed" can never be said of a run where they
/// did not execute.
/// </para>
/// </remarks>
public static class LoadGate
{
    public const string Variable = "EDITOR_LOAD";

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    /// <summary>Skips unless measurement was asked for.</summary>
    public static void Require() =>
        Assert.SkipUnless(
            Enabled,
            $"§8's measurements run only under {Variable}=1 (scripts/load.sh).");
}
