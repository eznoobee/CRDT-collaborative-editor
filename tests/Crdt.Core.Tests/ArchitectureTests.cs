using System.Reflection;
using System.Xml.Linq;
using Crdt.Core;

namespace Crdt.Core.Tests;

/// <summary>
/// Executable form of the dependency rule in PROJECT_SPEC.md §4.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms, because neither alone is sufficient:
/// </para>
/// <list type="bullet">
/// <item>
/// Reflecting over a compiled assembly shows which assemblies it actually
/// <em>uses</em>. This catches a forbidden package being referenced and used,
/// which the project graph cannot.
/// </item>
/// <item>
/// Parsing the project files shows which references are <em>declared</em>. This
/// is needed because the C# compiler elides references whose types are never
/// used, so an unused-but-declared reference is invisible to reflection.
/// </item>
/// </list>
/// </remarks>
public sealed class ArchitectureTests
{
    /// <summary>Assembly names <c>Crdt.Core</c> is permitted to use.</summary>
    private static bool IsBcl(AssemblyName name) =>
        name.Name is "netstandard" or "mscorlib" or "System"
        || name.Name?.StartsWith("System.", StringComparison.Ordinal) == true;

    /// <summary>Package prefixes that mark a project as touching infrastructure.</summary>
    private static readonly string[] InfrastructurePrefixes =
    [
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "StackExchange.Redis",
        "Microsoft.AspNetCore",
    ];

    /// <summary>
    /// The dependency graph from PROJECT_SPEC.md §4, as "project → what it may
    /// reference". Dependencies point inward; nothing points outward.
    /// </summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["Crdt.Core"] = [],
        ["Editor.Domain"] = ["Crdt.Core"],
        ["Editor.Infrastructure"] = ["Editor.Domain", "Crdt.Core"],
        ["Editor.Api"] = ["Editor.Infrastructure", "Editor.Domain", "Crdt.Core"],
    };

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

    [Fact]
    public void CrdtCore_uses_nothing_outside_the_BCL()
    {
        var offenders = typeof(ReplicaId).Assembly
            .GetReferencedAssemblies()
            .Where(a => !IsBcl(a))
            .Select(a => a.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Crdt.Core must reference nothing but the BCL (PROJECT_SPEC.md §4). "
            + $"Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EditorDomain_uses_no_infrastructure()
    {
        var offenders = typeof(Editor.Domain.Document).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null
                        && InfrastructurePrefixes.Any(p =>
                               n.StartsWith(p, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Editor.Domain must not reference infrastructure (PROJECT_SPEC.md §4). "
            + $"Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Declared_project_references_point_inward()
    {
        var src = RepoRoot().CreateSubdirectory("src");
        var violations = new List<string>();

        foreach (var (project, allowed) in Allowed)
        {
            var file = new FileInfo(Path.Combine(src.FullName, project, $"{project}.csproj"));
            Assert.True(file.Exists, $"Missing project file: {file.FullName}");

            var declared = XDocument.Load(file.FullName)
                .Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(v => v is not null)
                .Select(v => Path.GetFileNameWithoutExtension(v!.Replace('\\', '/')))
                .ToArray();

            violations.AddRange(
                declared.Except(allowed).Select(d => $"{project} → {d}"));
        }

        Assert.True(
            violations.Count == 0,
            "Project references must point inward (PROJECT_SPEC.md §4). "
            + $"Offending edges: {string.Join("; ", violations)}");
    }

    [Fact]
    public void CrdtCore_declares_no_packages_and_no_project_references()
    {
        var file = Path.Combine(
            RepoRoot().FullName, "src", "Crdt.Core", "Crdt.Core.csproj");
        var doc = XDocument.Load(file);

        var packages = doc.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .ToArray();
        var projects = doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .ToArray();

        // Checked separately from the reflection test: an unused package
        // reference would not show up in GetReferencedAssemblies().
        Assert.True(
            packages.Length == 0,
            $"Crdt.Core must declare no PackageReference (§4). Found: {string.Join(", ", packages)}");
        Assert.True(
            projects.Length == 0,
            $"Crdt.Core must declare no ProjectReference (§4). Found: {string.Join(", ", projects)}");
    }
}
