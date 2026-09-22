using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Editor.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without a running host.</summary>
/// <remarks>
/// Two jobs, and they need different things. Building a model — what
/// <c>migrations add</c> does — needs only a provider, and the fallback below
/// selects one without ever connecting. Applying a migration needs a real
/// database, so the connection string is taken from the environment when one is
/// there; the interop harness sets it, and so does anyone running
/// <c>database update</c> against something other than a local default.
/// <para>
/// The fallback carries no credentials. §7 allows a non-secret default in
/// source and nothing else, which is why the real one arrives by environment
/// rather than being written here.
/// </para>
/// </remarks>
public sealed class EditorDbContextFactory : IDesignTimeDbContextFactory<EditorDbContext>
{
    /// <summary>Where an explicit connection string comes from, in order.</summary>
    private static readonly string[] Sources =
        ["EDITOR_TEST_POSTGRES", "Postgres__ConnectionString"];

    public EditorDbContext CreateDbContext(string[] args)
    {
        var configured = Sources
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new EditorDbContext(
            new DbContextOptionsBuilder<EditorDbContext>()
                .UseNpgsql(configured ?? "Host=localhost;Database=editor;Username=editor")
                .Options);
    }
}
