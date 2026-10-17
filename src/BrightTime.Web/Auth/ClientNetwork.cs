using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace BrightTime.Web.Auth;

/// <summary>
/// Which reverse proxies may tell the app who the client is. Nothing is trusted by default.
/// </summary>
public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    /// <summary>Proxy addresses whose X-Forwarded-For and X-Forwarded-Proto are believed.</summary>
    public IList<string> TrustedProxies { get; init; } = [];

    /// <summary>Networks (CIDR) whose forwarded headers are believed, for proxies without a fixed address, such as containers.</summary>
    public IList<string> TrustedNetworks { get; init; } = [];

    /// <summary>
    /// How many proxy hops to unwind: 1 for nginx alone, 2 for a load balancer in front of
    /// nginx. Only entries appended by trusted hops are read, from the right; anything a client
    /// wrote into the header itself is ignored.
    /// </summary>
    public int ForwardLimit { get; set; } = 1;
}

/// <summary>
/// The client's real address behind a reverse proxy, and the address the rate limiter
/// counts by.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every request behind nginx comes from nginx's address, so all clients share
/// one rate-limit bucket: one person's failed sign-ins lock out everyone for the minute, and
/// the per-client limit protects nothing. With forwarded headers believed from anyone, a
/// client picks a new address per request and escapes the limit entirely. Hence an explicit
/// list of trusted proxies, and nothing trusted by default, not even loopback.
/// </para>
/// </remarks>
public static partial class ClientNetwork
{
    public static IServiceCollection AddClientNetwork(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        NetworkOptions network = configuration.GetSection(NetworkOptions.SectionName).Get<NetworkOptions>() ?? new();
        if (network.ForwardLimit < 1)
        {
            throw new InvalidOperationException("Network:ForwardLimit must be at least 1.");
        }

        List<IPAddress> proxies = [.. network.TrustedProxies.Select(p => IPAddress.TryParse(p, out IPAddress? a)
            ? a
            : throw new InvalidOperationException($"Network:TrustedProxies has '{p}', which is not an IP address."))];
        List<IPNetwork> networks = [.. network.TrustedNetworks.Select(n => IPNetwork.TryParse(n, out IPNetwork parsed)
            ? parsed
            : throw new InvalidOperationException($"Network:TrustedNetworks has '{n}', which is not a CIDR network."))];

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Careful: the middleware reads empty KnownProxies and KnownIPNetworks as "trust
            // every peer", not "trust none". So with nothing configured, forwarded headers are
            // switched off entirely rather than left with empty lists.
            bool anyTrusted = proxies.Count > 0 || networks.Count > 0;
            o.ForwardedHeaders = anyTrusted
                ? ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                : ForwardedHeaders.None;
            o.ForwardLimit = network.ForwardLimit;

            // The framework trusts loopback by default; nothing is trusted here unless configured.
            o.KnownProxies.Clear();
            o.KnownIPNetworks.Clear();
            foreach (IPAddress proxy in proxies)
            {
                o.KnownProxies.Add(proxy);
            }

            foreach (IPNetwork trusted in networks)
            {
                o.KnownIPNetworks.Add(trusted);
            }
        });

        return services;
    }

    /// <summary>Forwarded headers first, so everything after sees the client and its scheme.</summary>
    public static IApplicationBuilder UseClientNetwork(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseForwardedHeaders();

        // The misconfiguration that brings the shared bucket back: forwarded headers arriving
        // from a peer that is not trusted, so they were ignored. Said once, not per request.
        app.Use(async (context, next) =>
        {
            // Applied headers leave X-Original-For behind; unapplied ones do not.
            IHeaderDictionary headers = context.Request.Headers;
            if (headers.ContainsKey(ForwardedHeadersDefaults.XForwardedForHeaderName)
                && !headers.ContainsKey(ForwardedHeadersDefaults.XOriginalForHeaderName)
                && Interlocked.Exchange(ref _warned, 1) == 0)
            {
                LogUntrustedForwarder(
                    context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ClientNetwork)),
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            }

            await next(context).ConfigureAwait(false);
        });

        return app;
    }

    /// <summary>
    /// The rate limiter's key for a client: the IPv4 address, or the IPv6 /64. One IPv6 host
    /// can use any of the 2^64 addresses in its prefix, so counting by full address would let
    /// it rotate past any limit.
    /// </summary>
    public static string PartitionKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static int _warned;

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "X-Forwarded-For arrived from {Peer}, which is not a trusted proxy, so it was ignored and every client behind it shares one rate-limit bucket. If {Peer} is your reverse proxy, add it to Network:TrustedProxies or Network:TrustedNetworks.")]
    private static partial void LogUntrustedForwarder(ILogger logger, string peer);
}
