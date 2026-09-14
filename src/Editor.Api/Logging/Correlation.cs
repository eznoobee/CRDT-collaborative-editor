using Microsoft.AspNetCore.SignalR;

namespace Editor.Api.Logging;

/// <summary>§10's correlation id, on every log line a connection causes.</summary>
/// <remarks>
/// <para>
/// <strong>Per connection for the hub, per request for the document API</strong>,
/// and the difference is not cosmetic. A hub connection outlives every operation
/// sent over it, so the thing an operator needs to gather — everything one
/// client did — is keyed on the connection. A REST call has no connection, so
/// the request is the widest unit there is.
/// </para><para>
/// <strong>The failure this is shaped against is a per-invocation id.</strong>
/// Generating one where it is needed is easier than threading one from connect,
/// and it is invisible in any test that performs a single operation: every line
/// has an id, the id looks fine, and nothing can be gathered by it. It also
/// leaves <c>OnConnectedAsync</c> and <c>OnDisconnectedAsync</c> — the two
/// records an operator most wants when a client misbehaves — with no id at all.
/// </para><para>
/// Over WebSockets a connection is a single HTTP request, so a per-request id
/// would be stable across a connection too; the distinction that matters here is
/// against per-invocation, not against per-request.
/// </para>
/// </remarks>
public static partial class Correlation
{
    /// <summary>The scope key every log line carries.</summary>
    public const string Key = "correlationId";

    /// <summary>The hub's route, which the request middleware leaves alone.</summary>
    public const string HubPath = "/hub/editor";

    /// <summary>
    /// Puts a per-request correlation id on every log line a request causes.
    /// </summary>
    /// <remarks>
    /// The hub's own path is skipped. Its upgrade is one request that lives as
    /// long as the connection, so a request scope there would sit underneath
    /// every hub invocation and put a second, different value under the same
    /// key — which is worse than having none, because it reads as a working
    /// correlation id right up until someone tries to gather by it.
    /// </remarks>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(HubPath))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Editor.Api.Request");

            using (logger.BeginScope(new Dictionary<string, object?>
            {
                [Key] = context.TraceIdentifier,
            }))
            {
                // Method and path only. The query string carries §7's connect
                // ticket on the hub's own route, and §10 forbids a ticket
                // reaching any sink — so nothing here logs one, on any route.
                RequestLog.Received(logger, context.Request.Method, context.Request.Path.Value ?? "/");
                await next(context).ConfigureAwait(false);
            }
        });
    }
}

/// <summary>
/// Puts the connection's correlation id on every log line it causes (§10).
/// </summary>
/// <remarks>
/// A hub filter rather than something the hub calls, because the hub is not the
/// only thing that logs on a connection's behalf, and because a filter covers
/// connect and disconnect as well as invocations. §13.41's question answered
/// structurally: there is no method here for anyone to forget to call.
/// <para>
/// The id is SignalR's own connection id. Inventing a second one would mean two
/// identifiers for one connection and a join between them; the connection id is
/// already unique per connection, already in SignalR's diagnostics, and is
/// neither PII, document content, nor a token (§10).
/// </para>
/// </remarks>
public sealed partial class CorrelationHubFilter : IHubFilter
{
    private readonly ILoggerFactory _loggers;

    public CorrelationHubFilter(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _loggers = loggers;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);

        using (Scope(invocationContext.Context))
        {
            // The method name only. Arguments carry document content and
            // operation bytes, which §10 forbids in logs.
            Log.Invoked(Logger, invocationContext.HubMethodName);
            return await next(invocationContext).ConfigureAwait(false);
        }
    }

    public async Task OnConnectedAsync(
        HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using (Scope(context.Context))
        {
            Log.Opened(Logger);
            await next(context).ConfigureAwait(false);
        }
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        using (Scope(context.Context))
        {
            Log.Closed(Logger);
            await next(context, exception).ConfigureAwait(false);
        }
    }

    private IDisposable? Scope(HubCallerContext context) =>
        Logger.BeginScope(new Dictionary<string, object?>
        {
            [Correlation.Key] = context.ConnectionId,
        });

    private ILogger Logger => _loggers.CreateLogger(Category);

    /// <summary>The category the connection's own records are written under.</summary>
    public const string Category = "Editor.Api.Hubs.Connection";

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3450,
            Level = LogLevel.Debug,
            Message = "Connection opened.")]
        public static partial void Opened(ILogger logger);

        [LoggerMessage(
            EventId = 3451,
            Level = LogLevel.Debug,
            Message = "Connection closed.")]
        public static partial void Closed(ILogger logger);

        [LoggerMessage(
            EventId = 3452,
            Level = LogLevel.Debug,
            Message = "Invoked {Method}.")]
        public static partial void Invoked(ILogger logger, string method);
    }
}

/// <summary>The request record that carries §10's correlation id.</summary>
internal static partial class RequestLog
{
    [LoggerMessage(
        EventId = 3460,
        Level = LogLevel.Debug,
        Message = "Received {Method} {Path}.")]
    public static partial void Received(ILogger logger, string method, string path);
}
