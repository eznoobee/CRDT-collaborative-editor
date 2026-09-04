using Editor.Api.Authentication;
using Editor.Api.Documents;
using Editor.Api.Hubs;
using Editor.Api.Infrastructure;
using Editor.Api.Logging;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddEditorAuthentication(builder.Configuration);

// The connect tickets §7 requires live in Redis, because §8 forbids sticky
// sessions and the instance that issues a ticket is usually not the one that
// redeems it.
builder.Services.AddEditorRedis(builder.Configuration);

// Postgres, and §7's two-tier role lookup on top of it.
builder.Services.AddEditorPersistence(builder.Configuration);

var app = builder.Build();

// First in the pipeline, before anything reads the scheme or the client address.
app.UseForwardedHeaders();

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

// What the browser reads before it can log in (§7). Anonymous by necessity.
app.MapClientConfiguration();

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
