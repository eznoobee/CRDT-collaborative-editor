using Editor.Api.Authentication;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Infrastructure;
using Editor.Api.Logging;
using Microsoft.Extensions.Options;

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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddEditorAuthentication(builder.Configuration);

// The connect tickets §7 requires live in Redis, because §8 forbids sticky
// sessions and the instance that issues a ticket is usually not the one that
// redeems it.
builder.Services.AddEditorRedis(builder.Configuration);

// Postgres, and §7's two-tier role lookup on top of it.
builder.Services.AddEditorPersistence(builder.Configuration);

var app = builder.Build();

// Before authentication: the hub's credential is a ticket, not a token, and
// this refuses an unusable one while the client can still see the refusal.
app.UseConnectTicketGate("/hub/editor");

app.UseAuthentication();
app.UseAuthorization();

// PROJECT_SPEC.md §10 requires /health/live and /health/ready.
// Only liveness exists. Readiness must probe Postgres and Redis and report
// what it found; both are now wired up, so what is missing is the probe and
// its tests, which belong with the rest of §10's observability work in
// Phase 7. An endpoint that returned healthy without checking anything would
// be the hardcoded return §12 forbids, so it stays absent until then.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// Where the membership decision is made, with the OIDC token in a header.
app.MapNegotiate();

// The hub authenticates with the connect ticket in the query string, not the
// bearer token (§7), so it is not behind RequireAuthorization: the credential
// it accepts is redeemed in OnConnectedAsync, and a connection that arrives
// without a valid one is aborted there.
app.MapHub<EditorHub>("/hub/editor", options =>
{
    // §8 bounds the outbound buffer in bytes, because buffered payload is what
    // exhausts an app server. Past this the transport stops accepting more and
    // a send waits — which is why the fan-out puts a timeout on that wait
    // rather than letting one slow client stall the document.
    var backpressure = app.Services.GetRequiredService<IOptions<BackpressureOptions>>().Value;
    options.TransportMaxBufferSize = backpressure.MaxOutboundBytes;
    options.ApplicationMaxBufferSize = backpressure.MaxOutboundBytes;
});

await app.RunAsync();

/// <summary>Entry point marker, used by the integration tests to host the app.</summary>
public partial class Program { }
