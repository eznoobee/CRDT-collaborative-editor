using Microsoft.Extensions.Options;

namespace Editor.Api.Infrastructure;

/// <summary>Where this instance serves operational readings (§10).</summary>
public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>
    /// The port <c>/metrics</c> answers on. Zero disables it entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A port of its own, and that is the security control.</strong>
    /// §10's readings are operational data — how many documents are being
    /// edited, how many people are connected, how often submissions are being
    /// refused. None of it is content and none of it is PII, but it is not for
    /// the public either, and §4 publishes exactly one port: the proxy's. An
    /// admin listener the proxy does not forward to is unreachable from outside
    /// the deployment by construction rather than by a rule someone has to keep
    /// applying.
    /// </para><para>
    /// <strong>Not "authenticate the metrics endpoint" instead.</strong> That
    /// would make the readings reachable from the internet and then rely on a
    /// check — and §7's whole posture is that a route that should not exist
    /// beats a route that exists and is guarded. The guard is still asserted:
    /// a test fetches <c>/metrics</c> on the application port and requires a
    /// 404, because "we only bound it to the other port" is a claim about
    /// configuration until something checks it.
    /// </para><para>
    /// Zero by default, so an instance that was never told to serve readings
    /// serves none. §10's instruments still record; only the door is shut.
    /// </para>
    /// </remarks>
    public int Port { get; set; }
}

public static partial class AdminEndpoint
{
    /// <summary>Adds the admin listener, if one is configured.</summary>
    public static void AddAdminListener(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<AdminOptions>()
            .BindConfiguration(AdminOptions.Section)
            .Validate(
                options => options.Port is 0 or (>= 1024 and <= 65535),
                "The admin port must be zero (disabled) or an unprivileged port.")
            .ValidateOnStart();

        builder.Services.AddOptions<InstanceIdentity>()
            .BindConfiguration(InstanceIdentity.Section);

        builder.Services.AddSingleton(provider =>
            provider.GetRequiredService<IOptions<InstanceIdentity>>().Value);

        builder.Services.AddSingleton<MetricsSnapshot>();

        var port = builder.Configuration.GetValue<int>($"{AdminOptions.Section}:Port");
        if (port != 0)
        {
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(port));
        }
    }

    /// <summary>Maps <c>/metrics</c>, bound to the admin port alone.</summary>
    public static void MapAdminMetrics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var port = app.Services.GetRequiredService<IOptions<AdminOptions>>().Value.Port;
        if (port == 0)
        {
            return;
        }

        app.MapGet("/metrics", (MetricsSnapshot snapshot, HttpContext context) =>
            {
                // Checked here as well as bound there. Kestrel's host filtering
                // works on the Host header, which a caller supplies; the local
                // port is the connection's own and is not forgeable from
                // outside. Belt and braces on the one route that would leak
                // operational data if it answered on the public listener.
                if (context.Connection.LocalPort != port)
                {
                    return Results.NotFound();
                }

                return Results.Text(
                    snapshot.ToJson(), "application/json", System.Text.Encoding.UTF8);
            })
            .AllowAnonymous()
            .WithName("metrics");

        Log.Listening(app.Logger, port);
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 3460,
            Level = LogLevel.Information,
            Message = "Admin readings on port {Port} (PROJECT_SPEC.md §10).")]
        public static partial void Listening(ILogger logger, int port);
    }
}
