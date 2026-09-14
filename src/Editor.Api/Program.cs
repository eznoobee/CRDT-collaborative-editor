using Editor.Api.Authentication;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Infrastructure;
using Editor.Api.Logging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
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
// §4: TLS terminates at a reverse proxy, so the external scheme arrives in a
// header — trusted from the configured networks and nowhere else.
builder.Services.AddProxyForwarding(builder.Configuration);

// §10's readings, on a listener of its own that §4's proxy does not forward to.
builder.AddAdminListener();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddEditorAuthentication(builder.Configuration);
builder.Services.AddSecurityHeaders(builder.Configuration);

// The connect tickets §7 requires live in Redis, because §8 forbids sticky
// sessions and the instance that issues a ticket is usually not the one that
// redeems it.
builder.Services.AddEditorRedis(builder.Configuration);

// Postgres, and §7's two-tier role lookup on top of it.
builder.Services.AddEditorPersistence(builder.Configuration);

// §10's readiness, after both dependencies are registered because it probes
// through the application's own connections rather than opening its own.
builder.Services.AddEditorReadiness();

var app = builder.Build();

// §4's migrator: the same image, a different argument, and a process that
// applies the schema and exits. The serving path never migrates — §8 assumes
// several instances and they would race — but "the API does not migrate at
// startup" is a statement about the serving path, not about the assembly.
//
// This replaced an EF migrations bundle, which needed the SDK at build time and
// EF's design-time host at run time, and died in that host with a segfault the
// walk could see and nothing could explain. The application's own DbContext
// registration is the one the application uses; there is no second construction
// path to be wrong.
if (args.Contains("--migrate"))
{
    await app.Services.ApplyMigrationsAsync();
    return;
}

// First in the pipeline, before anything reads the scheme or the client address.
// §10's correlation id, first in the pipeline. Everything downstream logs
// inside its scope — including the refusals, which are the requests an operator
// most wants to gather. Placed here rather than beside authorization because a
// correlation id that covers only the handlers misses every line written by the
// middleware that refused before reaching one.
app.UseCorrelationId();

app.UseForwardedHeaders();

// Immediately after, and before anything that can produce a response. §7's
// headers belong on every one of them — the API's JSON, the SPA's index.html,
// the static assets and the error pages — because a control applied only to
// the endpoints someone remembered is a control that does not apply (5b.4).
app.UseSecurityHeaders();

// Before authentication: the hub's credential is a ticket, not a token, and
// this refuses an unusable one while the client can still see the refusal.
app.UseConnectTicketGate("/hub/editor");

app.UseAuthentication();
app.UseAuthorization();

// PROJECT_SPEC.md §10's two probes, and they answer different questions.
//
// Liveness says the process is running, which is what it says and all it should
// say (§13.28). It must NOT touch Postgres or Redis: an orchestrator restarts
// what fails liveness, so a liveness probe that failed on a Redis blip would
// roll the fleet over a dependency outage that the application is built to ride
// out (§10's reason for AbortOnConnectFail = false).
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// §10's instruments, readable. Bound to the admin port and 404 on every other,
// which HealthEndpointTests asserts rather than assumes.
app.MapAdminMetrics();

// Readiness says this instance can serve, and names the dependency when it
// cannot. Anonymous, like liveness: an orchestrator has no token, and the body
// carries dependency names and failure messages rather than anything about a
// document (§10 forbids PII, content, tokens and tickets in what is logged or
// exposed, and this is held to the same line).
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(Readiness.Ready),
    ResponseWriter = Readiness.WriteAsync,
});

// What the browser reads before it can log in (§7). Anonymous by necessity.
app.MapClientConfiguration();

// Where the membership decision is made, with the OIDC token in a header.
app.MapNegotiate();

// Documents and membership (§9). Until Phase 6 nothing in this product created
// either, and both harnesses seeded through psql (§13.27, register rows 15-16).
app.MapDocuments();

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

// The single-page application, served from this origin (§9, §13.26).
//
// Same origin, deliberately: the alternative is CORS, which means an
// allow-list of browser origins on an API that already accepts a bearer token
// and hands out connect tickets. Every one of those is a new way to get the
// configuration wrong in a direction that is permissive, and none of it buys
// anything the reverse proxy in front of a deployment cannot do by routing two
// paths to one host. Nothing here relaxes for development either — §7's
// no-fallback rule applies to origins as much as to issuers.
//
// Absent by default. A deployment with no built client serves the API alone
// rather than 404-ing every page from a directory that is not there, and the
// path is configuration rather than a convention so the e2e harness can point
// at a build it just made.
var spaRoot = builder.Configuration["Spa:RootPath"];
if (!string.IsNullOrWhiteSpace(spaRoot) && Directory.Exists(spaRoot))
{
    var files = new PhysicalFileProvider(Path.GetFullPath(spaRoot));

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

    // Client-side routing: any path the endpoints above did not claim is the
    // app's own. Registered last, so it cannot shadow /documents, /hub or
    // /health — MapFallbackToFile has the lowest possible order precisely so
    // that a new endpoint added later is still reached.
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = files });
}

await app.RunAsync();

/// <summary>Entry point marker, used by the integration tests to host the app.</summary>
public partial class Program { }
