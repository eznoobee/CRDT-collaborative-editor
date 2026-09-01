using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Editor.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without a running host.</summary>
/// <remarks>
/// The connection string here selects a provider; it is never used to connect.
/// It carries no credentials — §7 allows non-secret defaults in source and
/// nothing else.
/// </remarks>
public sealed class EditorDbContextFactory : IDesignTimeDbContextFactory<EditorDbContext>
{
    public EditorDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<EditorDbContext>()
            .UseNpgsql("Host=localhost;Database=editor;Username=editor")
            .Options);
}
