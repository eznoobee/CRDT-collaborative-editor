using Editor.Infrastructure.Tickets;

namespace Editor.Api.Hubs;

/// <summary>
/// Refuses a hub connection at the point where the client can still see it.
/// </summary>
/// <remarks>
/// The ticket is redeemed in <see cref="EditorHub.OnConnectedAsync"/>, because
/// that is the one place a single atomic redemption belongs. But a refusal
/// there is invisible: SignalR completes its handshake before invoking
/// OnConnectedAsync, so the client's StartAsync has already returned success by
/// the time the connection is torn down, and a client cannot tell "refused"
/// from "connected, then the server went away". A credential check whose
/// failure looks like a network blip is not much of a check.
/// <para>
/// So this runs on SignalR's own negotiate request — the first request of a
/// connection, before any transport exists — and answers 401 for a ticket that
/// is absent, malformed, expired or already spent. It is advisory: it does not
/// consume the ticket, and a client that skips negotiate bypasses it entirely.
/// The atomic redemption in the hub is what actually enforces single use, and
/// it is unchanged.
/// </para>
/// </remarks>
public static class ConnectTicketGate
{
    /// <summary>Rejects unusable tickets before a connection is established.</summary>
    public static IApplicationBuilder UseConnectTicketGate(this IApplicationBuilder app, string hubPath)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(hubPath);

        var negotiate = hubPath.TrimEnd('/') + "/negotiate";

        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals(negotiate, StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var tickets = context.RequestServices.GetRequiredService<IConnectTicketStore>();
            var ticket = context.Request.Query["access_token"].ToString();

            if (!await tickets.ExistsAsync(ticket, context.RequestAborted).ConfigureAwait(false))
            {
                // No body and no WWW-Authenticate challenge naming a scheme:
                // absent, expired, spent and forged all answer the same way,
                // because each difference is a fact about someone else's
                // session.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }
}
