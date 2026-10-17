using System.Net;
using System.Text.Json.Nodes;
using BrightTime.Modules.Platform.Tenancy;
using BrightTime.SharedKernel.Ids;
using BrightTime.Web.Tenancy;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// Tenants resolved from the request's host (decision D18), against the seeded database plus
/// tenants created here.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class TenancyTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private HttpClient ClientFor(string host)
    {
        HttpClient client = db.Factory.CreateClient(ConsoleFixture.ClientOptions(cookies: true));
        client.DefaultRequestHeaders.Host = host;
        return client;
    }

    private Browser BrowserFor(string host) => new(ClientFor(host));

    private async Task<HttpResponseMessage> GetAsync(string host, string path)
    {
        using HttpClient client = ClientFor(host);
        return await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    /// <summary>Creates a tenant with one primary domain, as provisioning would.</summary>
    private async Task CreateTenantAsync(string slug, string host, string status = "active", string locale = "en")
    {
        await using NpgsqlConnection connection = new(db.Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Guid id = Guid.CreateVersion7();
        await using NpgsqlCommand command = new(
            """
            insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone, status)
            values (@id, @slug, @name, 'Elsewhere County, New York', 'county', @locale, 'America/New_York', @status)
            on conflict (slug) do nothing;
            insert into platform.tenant_domain (tenant_id, host, is_primary)
            select id, @host, true from platform.tenant where slug = @slug
            on conflict do nothing;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", "BrightTime " + slug);
        command.Parameters.AddWithValue("locale", locale);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("host", host);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_unknown_host_is_a_404_that_names_no_tenant()
    {
        RequireDatabase();
        using HttpResponseMessage response = await GetAsync("nobody.localhost", "/search");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe("No BrightTime site is configured at this address.");
    }

    [Fact]
    public async Task An_alias_redirects_permanently_to_the_primary_host()
    {
        RequireDatabase();
        using HttpResponseMessage response = await GetAsync("kettle.brighttime.test", "/search?q=robotics");

        response.StatusCode.ShouldBe(HttpStatusCode.PermanentRedirect);
        response.Headers.Location!.ToString().ShouldBe("https://kettle.localhost/search?q=robotics");
    }

    [Theory]
    [InlineData("KETTLE.LOCALHOST")]
    [InlineData("kettle.localhost:8443")]
    [InlineData("kettle.localhost.")]
    public async Task The_host_is_normalised_before_it_is_looked_up(string host)
    {
        RequireDatabase();
        using HttpResponseMessage response = await GetAsync(host, "/search");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Another_tenant_on_its_own_host_sees_none_of_this_tenants_data()
    {
        RequireDatabase();
        await CreateTenantAsync("riverbend", "riverbend.localhost", locale: "es");
        string kettleSlug = db.Data.Listings.First(l => l.Status == "published").Slug;

        using HttpResponseMessage kettle = await GetAsync(ConsoleFixture.Host, $"/listings/{kettleSlug}");
        kettle.StatusCode.ShouldBe(HttpStatusCode.OK);

        using HttpResponseMessage riverbend = await GetAsync("riverbend.localhost", $"/listings/{kettleSlug}");
        riverbend.StatusCode.ShouldBe(HttpStatusCode.NotFound, "Kettle's listing does not exist on Riverbend's site");

        using HttpResponseMessage search = await GetAsync("riverbend.localhost", "/search");
        string html = await search.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        search.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("lang=\"es\"", Case.Sensitive, "the tenant's default locale");
        html.ShouldNotContain($"/listings/{kettleSlug}");
    }

    [Fact]
    public async Task A_tenant_that_is_not_active_is_unavailable()
    {
        RequireDatabase();
        await CreateTenantAsync("closedtown", "closedtown.localhost", status: "suspended");

        using HttpResponseMessage response = await GetAsync("closedtown.localhost", "/search");

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldNotContain("closedtown");
    }

    [Fact]
    public async Task Each_tenant_is_its_own_passkey_relying_party()
    {
        RequireDatabase();
        await CreateTenantAsync("lakeshire", "lakeshire.localhost");

        (await RelyingPartyAsync(ConsoleFixture.Host)).ShouldBe("kettle.localhost");
        (await RelyingPartyAsync("lakeshire.localhost")).ShouldBe("lakeshire.localhost");
    }

    private async Task<string> RelyingPartyAsync(string host)
    {
        using Browser browser = BrowserFor(host);
        await browser.GetAsync("/sign-in");
        (await browser.PostJsonAsync(Auth.PasskeyEndpoints.SignInOptionsPath, new JsonObject())).StatusCode.ShouldBe(HttpStatusCode.OK, browser.Html);
        return JsonNode.Parse(browser.Html)!["rpId"]!.GetValue<string>();
    }
}

/// <summary>The lookup cache and host normalisation, without a database.</summary>
public sealed class TenantLookupTests
{
    private sealed class CountingLookup : ITenantLookup
    {
        public int HostCalls { get; private set; }

        public Task<HostResolution?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
        {
            HostCalls++;
            return Task.FromResult(host == "kettle.localhost"
                ? new HostResolution(new TenantSite(new TenantId(Guid.NewGuid()), "Kettle", "active", "America/New_York", "en", "kettle.localhost"), true)
                : null);
        }

        public Task<TenantSite?> FindByIdAsync(TenantId tenant, CancellationToken cancellationToken = default) =>
            Task.FromResult<TenantSite?>(null);
    }

    [Fact]
    public async Task Found_and_missing_hosts_are_both_cached()
    {
        var inner = new CountingLookup();
        using var cached = new CachedTenantLookup(inner);

        (await cached.FindByHostAsync("kettle.localhost", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await cached.FindByHostAsync("kettle.localhost", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await cached.FindByHostAsync("made-up.example", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cached.FindByHostAsync("made-up.example", TestContext.Current.CancellationToken)).ShouldBeNull();

        inner.HostCalls.ShouldBe(2, "one query per host, found or not");
    }

    [Theory]
    [InlineData("Kettle.LocalHost", "kettle.localhost")]
    [InlineData("kettle.localhost:5176", "kettle.localhost")]
    [InlineData("kettle.localhost.", "kettle.localhost")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("127.0.0.1", null)]
    [InlineData("[::1]:443", null)]
    [InlineData("evil host", null)]
    [InlineData("under_score.example", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Hosts_are_normalised_as_the_domain_table_stores_them(string? host, string? expected) =>
        HostNames.Normalise(host).ShouldBe(expected);
}
