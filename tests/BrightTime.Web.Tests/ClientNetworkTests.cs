using System.Net;
using BrightTime.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BrightTime.Web.Tests;

/// <summary>
/// The sign-in rate limit behind a reverse proxy: each real client gets its own allowance,
/// and nobody can claim a new address to get a fresh one. A startup filter plays the TCP peer
/// (nginx, or anyone else), ahead of the app's own pipeline.
/// </summary>
public sealed class ClientNetworkTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Proxy = "10.0.0.5";
    private const string Limited = "/account/forgot";

    private WebApplicationFactory<Program> App(bool trustProxy = true) => factory.WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Development");
        b.UseSetting("Auth:SignInAttemptsPerMinute", "2");
        if (trustProxy)
        {
            b.UseSetting("Network:TrustedProxies:0", Proxy);
        }

        b.ConfigureServices(s => s.AddSingleton<IStartupFilter, PeerFilter>());
    });

    private static async Task<HttpStatusCode> PostAsync(HttpClient client, string peer, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Limited, UriKind.Relative))
        {
            Content = new FormUrlEncodedContent([new("email", "someone@example.org")]),
        };
        request.Headers.Add(PeerFilter.Header, peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task Behind_the_trusted_proxy_each_client_has_its_own_allowance()
    {
        await using WebApplicationFactory<Program> app = App();
        using HttpClient client = app.CreateClient();

        List<HttpStatusCode> first = [];
        for (int i = 0; i < 3; i++)
        {
            first.Add(await PostAsync(client, Proxy, "203.0.113.1"));
        }

        first[2].ShouldBe(HttpStatusCode.TooManyRequests);
        first.Take(2).ShouldAllBe(s => s != HttpStatusCode.TooManyRequests);

        // Another person behind the same nginx is not locked out by the first.
        (await PostAsync(client, Proxy, "203.0.113.2")).ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_peer_are_ignored()
    {
        await using WebApplicationFactory<Program> app = App();
        using HttpClient client = app.CreateClient();

        // A client talking to the app directly, claiming a new address each time.
        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 3; i++)
        {
            statuses.Add(await PostAsync(client, "198.51.100.7", $"203.0.113.{i + 10}"));
        }

        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests, "counted by its real address, not the claimed ones");
    }

    [Fact]
    public async Task A_client_cannot_prepend_its_way_to_a_fresh_allowance()
    {
        await using WebApplicationFactory<Program> app = App();
        using HttpClient client = app.CreateClient();

        // nginx appends the real address; the client controls everything to its left.
        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 3; i++)
        {
            statuses.Add(await PostAsync(client, Proxy, $"192.0.2.{i + 1}, 203.0.113.9"));
        }

        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task IPv6_clients_are_counted_by_their_slash_64()
    {
        await using WebApplicationFactory<Program> app = App();
        using HttpClient client = app.CreateClient();

        List<HttpStatusCode> sameNetwork = [];
        for (int i = 1; i <= 3; i++)
        {
            sameNetwork.Add(await PostAsync(client, Proxy, $"2001:db8:1:2::{i}"));
        }

        sameNetwork[2].ShouldBe(HttpStatusCode.TooManyRequests, "rotating within one /64 does not reset the count");
        (await PostAsync(client, Proxy, "2001:db8:1:3::1")).ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Nothing_is_trusted_by_default_not_even_loopback()
    {
        await using WebApplicationFactory<Program> app = App(trustProxy: false);
        ForwardedHeadersOptions options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        // Empty trust lists would mean "trust everyone" to the middleware, so headers are off.
        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.None);
        options.KnownProxies.ShouldBeEmpty();
        options.KnownIPNetworks.ShouldBeEmpty();

        using HttpClient client = app.CreateClient();
        List<HttpStatusCode> statuses = [];
        for (int i = 0; i < 3; i++)
        {
            statuses.Add(await PostAsync(client, "127.0.0.1", $"203.0.113.{i + 20}"));
        }

        statuses[2].ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_limited_request_is_told_why_and_when_to_retry()
    {
        await using WebApplicationFactory<Program> app = App();
        using HttpClient client = app.CreateClient();
        for (int i = 0; i < 2; i++)
        {
            await PostAsync(client, Proxy, "203.0.113.60");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Limited, UriKind.Relative))
        {
            Content = new FormUrlEncodedContent([new("email", "someone@example.org")]),
        };
        request.Headers.Add(PeerFilter.Header, Proxy);
        request.Headers.Add("X-Forwarded-For", "203.0.113.60");
        using HttpResponseMessage limited = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Not re-executed through the not-found page, which turned this into "does not exist" or a 400.
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(60));
        (await limited.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Too many attempts. Wait a minute and try again.");
    }

    [Fact]
    public async Task A_configured_proxy_turns_both_headers_on()
    {
        await using WebApplicationFactory<Program> app = App();
        ForwardedHeadersOptions options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardedHeaders.ShouldBe(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        options.KnownProxies.ShouldBe([IPAddress.Parse(Proxy)]);
        options.ForwardLimit.ShouldBe(1);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData(null, "unknown")]
    public void The_partition_key_is_the_IPv4_address_or_the_IPv6_slash_64(string? address, string key) =>
        ClientNetwork.PartitionKey(address is null ? null : IPAddress.Parse(address)).ShouldBe(key);

    [Fact]
    public void A_misconfigured_proxy_list_stops_startup()
    {
        Should.Throw<InvalidOperationException>(() =>
        {
            using WebApplicationFactory<Program> broken = factory.WithWebHostBuilder(b =>
                b.UseSetting("Network:TrustedProxies:0", "nginx"));
            _ = broken.Services;
        }).Message.ShouldContain("not an IP address");
    }

    /// <summary>Stands in for the TCP connection: who is actually talking to the app.</summary>
    private sealed class PeerFilter : IStartupFilter
    {
        public const string Header = "X-Test-Peer";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[Header].ToString() is { Length: > 0 } p ? p : Proxy);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
