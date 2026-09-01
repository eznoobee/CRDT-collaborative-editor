var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// PROJECT_SPEC.md §10 requires /health/live and /health/ready.
// Only liveness exists in Phase 0: readiness must check Postgres and Redis,
// and neither is wired up until Phase 2. Adding a readiness endpoint that
// reports healthy without checking anything would be a hardcoded return
// (§12, "no stubs presented as done"), so it is deliberately absent.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

await app.RunAsync();

/// <summary>Entry point marker, used by the integration tests to host the app.</summary>
public partial class Program { }
