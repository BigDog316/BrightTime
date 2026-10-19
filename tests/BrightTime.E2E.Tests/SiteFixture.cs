using System.Net;
using System.Net.Sockets;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Npgsql;

namespace BrightTime.E2E.Tests;

/// <summary>
/// The web app over a seeded database of its own, served by Kestrel on a real port, with
/// Chromium and WebKit driving it. Needs <c>BRIGHTTIME_TEST_CONNECTION</c> (a superuser) and the
/// Playwright browsers; without either the tests skip, except under CI, where a missing browser
/// fails the run rather than hiding the gate.
/// </summary>
/// <remarks>
/// The site is addressed as <c>kettle.localhost</c>, the tenant's primary domain, because the
/// tenant comes from the host name; browsers resolve <c>*.localhost</c> to loopback themselves.
/// Install the browsers once with
/// <c>pwsh artifacts/bin/BrightTime.E2E.Tests/debug/playwright.ps1 install chromium webkit</c>.
/// </remarks>
public sealed class SiteFixture : IAsyncLifetime
{
    public const string DatabaseName = "brighttime_e2etest";
    private const string Password = "bt-test-only";

    private WebApplicationFactory<Program>? _root;
    private WebApplicationFactory<Program>? _factory;
    private IPlaywright? _playwright;
    private readonly Dictionary<string, IBrowser> _browsers = new(StringComparer.Ordinal);

    public string? SuperuserConnection { get; } = Environment.GetEnvironmentVariable("BRIGHTTIME_TEST_CONNECTION");

    /// <summary>Why the tests cannot run here, or null when they can.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>The site's address, as a family would type it.</summary>
    public Uri BaseUrl { get; private set; } = default!;

    internal SeedData Data { get; private set; } = default!;


    public string Connection => new NpgsqlConnectionStringBuilder(SuperuserConnection) { Database = DatabaseName }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (SuperuserConnection is null)
        {
            SkipReason = "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.";
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        _playwright = await Playwright.CreateAsync();
        try
        {
            _browsers[Engines.Chromium] = await _playwright.Chromium.LaunchAsync();
            _browsers[Engines.WebKit] = await _playwright.Webkit.LaunchAsync();
        }
        catch (PlaywrightException ex) when (Environment.GetEnvironmentVariable("CI") is null)
        {
            SkipReason = "The Playwright browsers are not installed. Run "
                + "pwsh artifacts/bin/BrightTime.E2E.Tests/debug/playwright.ps1 install chromium webkit. "
                + ex.Message.Split('\n')[0];
            return;
        }

        await EnsureLoginsAsync(ct);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone);
        Data = await SeedDatabase.RecreateAsync(
            Connection, SeedDatabase.FindSchemaDirectory(AppContext.BaseDirectory),
            SystemClock.Instance.TodayIn(zone), null, ct);

        // The tenant's address carries the port, so the port is chosen before the host starts.
        int port = FreePort();
        BaseUrl = new Uri($"http://{KettleSeed.Hosts[0]}:{port}/");

        string As(string login) =>
            new NpgsqlConnectionStringBuilder(Connection) { Username = login, Password = Password }.ConnectionString;

        _root = new WebApplicationFactory<Program>();
        _factory = _root.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Finder:Mode", "Sql");
            b.UseSetting("Tenancy:Mode", "Host");
            b.UseSetting("Tenancy:PublicScheme", "http");
            b.UseSetting("Tenancy:PublicPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            b.UseSetting("Finder:ConnectionString", As("bt_web_login"));
            b.UseSetting("Auth:ConnectionString", As("bt_auth_login"));
            b.UseSetting("Auth:DevelopmentSignIn", "true");

            // Through the host's urls setting: UseKestrel(port) on this derived factory left the server on
            // localhost:5000. localhost listens on IPv4 and IPv6 loopback.
            b.UseSetting(WebHostDefaults.ServerUrlsKey, $"http://localhost:{port}");
        });
        _factory.UseKestrel();
        _factory.StartServer();
    }

    /// <summary>A fresh browser context (its own cookies) on one engine, with or without JavaScript.</summary>
    public async Task<IPage> NewPageAsync(string engine, bool javaScript = true, string? locale = null)
    {
        IBrowserContext context = await _browsers[engine].NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl.AbsoluteUri,
            JavaScriptEnabled = javaScript,
            Locale = locale,
        });
        return await context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IBrowser browser in _browsers.Values)
        {
            await browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_root is not null)
        {
            await _root.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task EnsureLoginsAsync(CancellationToken ct)
    {
        await using NpgsqlConnection connection = new(SuperuserConnection);
        await connection.OpenAsync(ct);
        await using NpgsqlCommand command = new(
            $$"""
            do $$
            begin
              if not exists (select 1 from pg_roles where rolname = 'bt_web_login') then
                create role bt_web_login login password '{{Password}}';
              end if;
              if not exists (select 1 from pg_roles where rolname = 'bt_auth_login') then
                create role bt_auth_login login password '{{Password}}';
              end if;
              if not exists (select 1 from pg_roles where rolname = 'brighttime_app') then
                raise exception 'Apply db/schema once on this server first: the brighttime roles are missing.';
              end if;
            end $$;
            grant brighttime_app, brighttime_finder to bt_web_login;
            grant brighttime_auth to bt_auth_login;
            """, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>The browser engines the gate runs on, by name, for test data.</summary>
public static class Engines
{
    public const string Chromium = "chromium";
    public const string WebKit = "webkit";

    public static TheoryData<string> All => [Chromium, WebKit];
}

[CollectionDefinition(Name)]
public sealed class Site : ICollectionFixture<SiteFixture>
{
    public const string Name = "site";
}
