using System.Text.RegularExpressions;

namespace Editor.Api.Tests;

/// <summary>
/// The breakdown gate's exemption list holds exactly one phase.
/// </summary>
/// <remarks>
/// <para>
/// 9.0 added §12's questions 4 and 5 to <c>check-breakdown.sh</c> and exempted
/// Phase 7b's breakdown, which was written before those questions existed.
/// Retrofitting would have meant inventing eleven tasks' worth of answers after
/// the work was done, and a question is worth something only when it is asked
/// beforehand — so the exemption is right, and it is also a hole one edit wide.
/// </para><para>
/// <b>An exemption list that can grow silently is the gate's hole moved one
/// level up.</b> A gate that requires a field, plus a list of documents that do
/// not have to have it, is exactly as strong as the discipline around the list —
/// which is the thing §13.43 says will not hold, and §13.50 says will not hold
/// specifically for lists that are maintained by remembering.
/// </para><para>
/// So the list is pinned here, in a different file from itself. Adding a phase
/// to it is then two edits in two places rather than one, the second of which
/// is a failing test that has to be deliberately updated — which is the point.
/// The message says what an honest update requires: a reason recorded in the
/// spec, not a name appended to a set.
/// </para><para>
/// <b>Vacuity risk, named before this was written.</b> A test asserting that a
/// set contains what the file says it contains would pass against any list at
/// all, because both sides would come from the same place — §13.42's shape. So
/// the expected value below is a literal written out here, and the parse is
/// checked to have found something before it is compared, because a regex that
/// silently matches nothing would make an empty set equal to an empty
/// expectation.
/// </para>
/// </remarks>
public sealed class BreakdownExemptionTests
{
    /// <summary>
    /// The phases exempt from §12's later questions, written out rather than read.
    /// </summary>
    /// <remarks>
    /// Phase 7b, and nothing else. Questions 4 and 5 entered §12 during 7b
    /// itself — in 7b.5 and 7b.7 — so its breakdown predates them; every
    /// breakdown written afterwards had them available and must answer them.
    /// </remarks>
    private static readonly string[] Expected = ["7b"];

    [Fact]
    public void The_gate_exempts_exactly_phase_7b()
    {
        var script = File.ReadAllText(
            Path.Combine(RepoRoot().FullName, "scripts", "check-breakdown.sh"));

        var declaration = Regex.Match(
            script, @"EXEMPT_FROM_LATER\s*=\s*\{([^}]*)\}");

        // Checked before the comparison: a regex that stopped matching would
        // leave an empty set to compare against an empty parse, and the test
        // would pass while the list said anything at all.
        Assert.True(
            declaration.Success,
            "check-breakdown.sh no longer declares EXEMPT_FROM_LATER in a form this "
                + "test can read. The list is still there; this test has gone blind.");

        var exempt = Regex.Matches(declaration.Groups[1].Value, @"""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .OrderBy(phase => phase, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            exempt.Length > 0,
            "EXEMPT_FROM_LATER parsed as empty. Either the list was emptied — in "
                + "which case delete this test and the exemption together — or the "
                + "parse broke.");

        Assert.Equal(Expected, exempt);
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any())
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir;
    }
}
