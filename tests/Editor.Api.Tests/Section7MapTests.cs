using System.Text.RegularExpressions;

namespace Editor.Api.Tests;

/// <summary>
/// §7's requirement map: every requirement has a row, and every row is real.
/// </summary>
/// <remarks>
/// <para>
/// §7 requires this map and requires that <b>the list is derived from §7's text
/// rather than from the suite</b>. Deriving it from the tests would make it a
/// restatement of whatever happens to be written — a document that cannot
/// disagree with the code, which is §13.22's shape. So the bullets are read out
/// of PROJECT_SPEC.md and the map has to account for all of them.
/// </para><para>
/// The vacuity risks, named before this was written.
/// </para><para>
/// <b>First: a map is a document, and a document passes any test that only
/// checks it parses.</b> Three things are checked instead, each of which can be
/// false on its own — every §7 bullet has a row, every row's anchor still
/// appears in §7, and every test a row names exists in the repository. The
/// third is what stops a row being a wish.
/// </para><para>
/// <b>Second: an anchor that matches loosely matches everything.</b> Anchors are
/// compared as verbatim substrings of the bullet, so a row cannot drift onto a
/// neighbouring requirement, and each bullet must be claimed by exactly one row
/// — two rows sharing a bullet would leave another bullet silently uncovered
/// while the counts still balanced.
/// </para><para>
/// <b>Third: this test proves the map is complete, not that the requirements
/// hold.</b> Stated in the map itself and repeated here so nobody reads a green
/// run as more than it is. What establishes that a named test reaches its
/// mechanism is §12's sabotage practice.
/// </para>
/// </remarks>
public sealed partial class Section7MapTests
{
    /// <summary>Where a named test could live.</summary>
    private static readonly string[] SearchedTrees = ["tests", "client/src", "src"];

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

    /// <summary>A row of the map: the anchor, and the tests it names.</summary>
    private sealed record Row(int Number, string Anchor, string ProvenBy);

    /// <summary>§7's top-level requirement bullets, in order.</summary>
    private static List<string> Requirements()
    {
        var spec = File.ReadAllText(Path.Combine(RepoRoot().FullName, "PROJECT_SPEC.md"));
        var start = spec.IndexOf("\n## 7. ", StringComparison.Ordinal);
        var end = spec.IndexOf("\n## 8. ", start, StringComparison.Ordinal);

        Assert.True(start > 0 && end > start, "§7 was not found in PROJECT_SPEC.md");

        var section = spec[start..end];
        var bullets = new List<string>();
        string? current = null;

        foreach (var line in section.Split('\n'))
        {
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    bullets.Add(current);
                }

                current = line[2..];
            }
            else if (current is not null)
            {
                // A bullet's continuation lines and its indented paragraphs
                // belong to it; anything starting at column zero ends it.
                if (line.StartsWith("  ", StringComparison.Ordinal) || line.Length == 0)
                {
                    current += "\n" + line;
                }
                else
                {
                    bullets.Add(current);
                    current = null;
                }
            }
        }

        if (current is not null)
        {
            bullets.Add(current);
        }

        return bullets;
    }

    private static List<Row> Map()
    {
        var path = Path.Combine(RepoRoot().FullName, "docs", "section-7-map.md");
        Assert.True(File.Exists(path), $"the §7 map is missing: {path}");

        var rows = new List<Row>();
        foreach (var line in File.ReadAllLines(path))
        {
            var match = MapRow().Match(line);
            if (!match.Success)
            {
                continue;
            }

            rows.Add(new Row(
                int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                Unquote(match.Groups[2].Value),
                match.Groups[4].Value));
        }

        return rows;
    }

    /// <summary>Strips the backticks the anchor column is written in.</summary>
    private static string Unquote(string cell)
    {
        var trimmed = cell.Trim();
        if (trimmed.StartsWith('`') && trimmed.EndsWith('`') && trimmed.Length > 1)
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Trim();
    }

    [Fact]
    public void Every_requirement_in_section_7_has_exactly_one_row()
    {
        var requirements = Requirements();
        var rows = Map();

        Assert.NotEmpty(requirements);

        var uncovered = new List<string>();
        var contested = new List<string>();

        foreach (var requirement in requirements)
        {
            var claiming = rows
                .Where(row => requirement.Contains(row.Anchor, StringComparison.Ordinal))
                .ToList();

            var opening = requirement.Split('\n')[0];
            if (claiming.Count == 0)
            {
                uncovered.Add(opening);
            }
            else if (claiming.Count > 1)
            {
                contested.Add($"{opening} — claimed by rows {string.Join(", ", claiming.Select(r => r.Number))}");
            }
        }

        Assert.True(
            uncovered.Count == 0,
            "§7 requirements with no row in docs/section-7-map.md:\n  "
                + string.Join("\n  ", uncovered));

        // Two rows on one bullet is not a harmless duplicate: the counts still
        // balance, so another bullet can be uncovered without the total
        // changing.
        Assert.True(
            contested.Count == 0,
            "§7 requirements claimed by more than one row:\n  " + string.Join("\n  ", contested));
    }

    [Fact]
    public void Every_row_anchors_to_text_that_is_still_in_section_7()
    {
        // The other direction. Without it the map keeps rows for requirements
        // that have been reworded or deleted, and a stale map reads exactly
        // like a current one.
        var section = string.Join("\n", Requirements());
        var orphaned = Map()
            .Where(row => !section.Contains(row.Anchor, StringComparison.Ordinal))
            .Select(row => $"row {row.Number}: \"{row.Anchor}\"")
            .ToList();

        Assert.True(
            orphaned.Count == 0,
            "rows anchored to text §7 no longer contains:\n  " + string.Join("\n  ", orphaned));
    }

    [Fact]
    public void Every_test_a_row_names_exists()
    {
        // What stops a row being a wish. A map naming NegotiateTests proves
        // nothing if no such thing was ever written, and that is the state the
        // map would have been born in if it had been filled in from §7 alone.
        var root = RepoRoot();
        var sources = SearchedTrees
            .Select(part => new DirectoryInfo(Path.Combine(root.FullName, part.Replace('/', Path.DirectorySeparatorChar))))
            .Where(dir => dir.Exists)
            .SelectMany(dir => dir.EnumerateFiles("*", SearchOption.AllDirectories))
            .Where(file => file.Extension is ".cs" or ".ts" or ".tsx")
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.FullName.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var haystack = string.Join("\n", sources.Select(file => file.Name + "\n" + File.ReadAllText(file.FullName)));
        var missing = new List<string>();

        foreach (var row in Map())
        {
            foreach (var named in row.ProvenBy.Split(','))
            {
                var name = named.Trim().Trim('`', '*', ' ');
                if (name.Length == 0)
                {
                    continue;
                }

                if (!haystack.Contains(name, StringComparison.Ordinal))
                {
                    missing.Add($"row {row.Number} names \"{name}\", which does not exist");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n  ", missing));
    }

    [Fact]
    public void The_hsts_divergence_has_its_own_row()
    {
        // §7 names one deliberate test/production difference and requires it
        // here rather than in a compose comment alone, because a comment in a
        // compose file is exactly the artefact that stops being read. Checked
        // mechanically so that removing the row fails rather than going quiet.
        var map = File.ReadAllText(
            Path.Combine(RepoRoot().FullName, "docs", "section-7-map.md"));

        Assert.Contains("Strict-Transport-Security", map, StringComparison.Ordinal);
        Assert.Contains("365 days", map, StringComparison.Ordinal);
        Assert.Contains("60 seconds", map, StringComparison.Ordinal);

        // And the shipped default is the strict one, so a deployment that
        // configures nothing is protected rather than exposed.
        var options = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src", "Editor.Api", "Infrastructure", "SecurityHeaders.cs"));

        Assert.Contains("ProductionHstsMaxAge", options, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\|\s*(\d+)\s*\|(.+?)\|(.+?)\|(.+?)\|\s*$")]
    private static partial Regex MapRow();
}
