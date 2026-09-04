using Editor.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Editor.Api.Infrastructure;

/// <summary>
/// Applying the schema, as its own process (PROJECT_SPEC.md §4).
/// </summary>
/// <remarks>
/// Nothing else does it. Not the API's serving path — §8 forbids sticky
/// sessions and therefore assumes several instances, which would race the same
/// migration and turn a rolling deploy into a startup timeout — and not an
/// operator, because an operator step is what this project already had and
/// register row 19 is the result.
/// </remarks>
public static class MigrationExtensions
{
    public static async Task ApplyMigrationsAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<EditorDbContext>();

        // Reported rather than silent. A migrator that prints nothing and exits
        // zero is indistinguishable from one that did nothing, and §13.28 is
        // what that costs.
        var pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();
        Console.WriteLine(pending.Length == 0
            ? "Schema is up to date; nothing to apply."
            : $"Applying {pending.Length} migration(s): {string.Join(", ", pending)}");

        await context.Database.MigrateAsync();
        Console.WriteLine("Schema applied.");
    }
}
