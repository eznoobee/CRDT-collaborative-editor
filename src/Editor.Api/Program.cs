using Editor.Api.Authentication;
using Editor.Api.Infrastructure;
using Editor.Api.Logging;

var builder = WebApplication.CreateBuilder(args);

// First, and before anything that could log. §7: a ticket or a token must
// never reach a sink, and the request URL — query string included — is logged
// by the hosting layer before the first middleware runs, so redaction cannot
// be a middleware.
builder.Services.AddSecretRedaction();

// Validation is registered here and enforced at host start, so a deployment
// that forgot to configure an issuer never reaches a listening state rather
// than starting up and accepting whatever arrives (§7, §13.12).
// appsettings.json deliberately carries no Oidc defaults for the same reason:
// a default issuer is a fallback, and a fallback is what §7 forbids.
builder.Services.AddEditorAuthentication(builder.Configuration);

// The connect tickets §7 requires live in Redis, because §8 forbids sticky
// sessions and the instance that issues a ticket is usually not the one that
// redeems it.
builder.Services.AddEditorRedis(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// PROJECT_SPEC.md §10 requires /health/live and /health/ready.
// Only liveness exists in Phase 0: readiness must check Postgres and Redis,
// and neither is wired up until Phase 2. Adding a readiness endpoint that
// reports healthy without checking anything would be a hardcoded return
// (§12, "no stubs presented as done"), so it is deliberately absent.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

await app.RunAsync();

/// <summary>Entry point marker, used by the integration tests to host the app.</summary>
public partial class Program { }
