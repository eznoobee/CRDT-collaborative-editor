using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Editor.Api.Infrastructure;

/// <summary>
/// Learning the external scheme from the reverse proxy (PROJECT_SPEC.md §4).
/// </summary>
/// <remarks>
/// TLS terminates at a proxy, so the API sees plaintext and would report itself
/// as `http` to every redirect, cookie policy and HSTS decision that asks. The
/// forwarded headers correct that.
/// <para>
/// <b>Accepted from configured networks only.</b> A client-supplied
/// <c>X-Forwarded-Proto</c> would let a plaintext request assert that it arrived
/// over TLS, which hands §7's transport-security decisions to the caller — the
/// header is trusted precisely as far as the network it came from is. The
/// framework's defaults (loopback only) are cleared and replaced, rather than
/// added to, because a proxy on a private network is not loopback and a
/// deployment that silently trusts both is trusting more than it configured.
/// </para>
/// </remarks>
public static class ForwardedHeadersExtensions
{
    public const string Section = "ForwardedHeaders";

    public static IServiceCollection AddProxyForwarding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var configured = configuration[$"{Section}:KnownProxies"];

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;

            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            if (string.IsNullOrWhiteSpace(configured))
            {
                // Nothing configured means no proxy, which means no forwarded
                // header is trusted from anywhere. A deployment behind a proxy
                // that forgot to configure this reports http and behaves as if
                // it were not behind one — visibly wrong, rather than silently
                // trusting whatever arrived.
                return;
            }

            foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            {
                if (entry.Contains('/'))
                {
                    var parts = entry.Split('/', 2);
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                        IPAddress.Parse(parts[0]),
                        int.Parse(parts[1], CultureInfo.InvariantCulture)));
                }
                else
                {
                    options.KnownProxies.Add(IPAddress.Parse(entry));
                }
            }
        });

        return services;
    }
}
