using System.Net;
using System.Text.RegularExpressions;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace BrightTime.Web.Tests;

/// <summary>
/// A seeded database of its own and the web app over it in SQL mode, with development
/// sign-in. Needs <c>BRIGHTTIME_TEST_CONNECTION</c> (a superuser); without it the tests skip.
/// </summary>
/// <remarks>
/// The logins mirror production's split: the web login is a member of brighttime_app and
/// brighttime_finder, and sign-in reads accounts as brighttime_auth. Roles are cluster-wide,
/// so they are created if absent and never altered otherwise.
/// </remarks>
public sealed partial class ConsoleFixture : IAsyncLifetime
{
    public const string DatabaseName = "brighttime_consoletest";
    private const string Password = "bt-test-only";

    private WebApplicationFactory<Program>? _root;
    private WebApplicationFactory<Program>? _factory;

    public string? SuperuserConnection { get; } = Environment.GetEnvironmentVariable("BRIGHTTIME_TEST_CONNECTION");

    public bool IsAvailable => SuperuserConnection is not null;

    public string Connection => new NpgsqlConnectionStringBuilder(SuperuserConnection) { Database = DatabaseName }.ConnectionString;

    internal SeedData Data { get; private set; } = default!;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("No database.");

    public async ValueTask InitializeAsync()
    {
        if (SuperuserConnection is null)
        {
            return;
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        await EnsureLoginsAsync(ct);

        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone);
        Data = await SeedDatabase.RecreateAsync(
            Connection, SeedDatabase.FindSchemaDirectory(AppContext.BaseDirectory),
            SystemClock.Instance.TodayIn(zone), null, ct);

        string As(string login) =>
            new NpgsqlConnectionStringBuilder(Connection) { Username = login, Password = Password }.ConnectionString;

        _root = new WebApplicationFactory<Program>();
        _factory = _root.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("Finder:Mode", "Sql");
            b.UseSetting("Finder:TenantId", KettleSeed.TenantId.ToString());
            b.UseSetting("Finder:ConnectionString", As("bt_web_login"));
            b.UseSetting("Auth:ConnectionString", As("bt_auth_login"));
            b.UseSetting("Auth:DevelopmentSignIn", "true");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_root is not null)
        {
            await _root.DisposeAsync();
        }
    }

    public Browser NewBrowser() => new(Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test assertions written as literals; values are bound parameters.")]
    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await using NpgsqlConnection connection = new(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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
            """, connection);
        await command.ExecuteNonQueryAsync(ct);

        // Grants after the schema exists, because the roles are created by 00-extensions-roles.sql.
        await using NpgsqlCommand grants = new(
            "grant brighttime_app, brighttime_finder to bt_web_login; grant brighttime_auth to bt_auth_login;", connection);
        await grants.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// A cookie-keeping client that speaks to the server-rendered forms: it reads the antiforgery
/// token from the page it is on and names the form it posts, as a browser does.
/// </summary>
public sealed partial class Browser(HttpClient client) : IDisposable
{
    public string Html { get; private set; } = string.Empty;

    public async Task<HttpResponseMessage> GetAsync(string path)
    {
        HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response;
    }

    /// <summary>Posts to a page's form. <paramref name="handler"/> is its <c>@formname</c>; null for a plain endpoint.</summary>
    public async Task<HttpResponseMessage> PostAsync(string path, string? handler, params (string Name, string Value)[] fields)
    {
        List<KeyValuePair<string, string>> body = [.. fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))];
        body.Add(new("__RequestVerificationToken", Token()));
        if (handler is not null)
        {
            body.Add(new("_handler", handler));
        }

        using var content = new FormUrlEncodedContent(body);
        HttpResponseMessage response = await client.PostAsync(new Uri(path, UriKind.Relative), content, TestContext.Current.CancellationToken);
        Html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return response;
    }

    public async Task SignInAsync(string email)
    {
        await GetAsync("/dev/sign-in");
        HttpResponseMessage response = await PostAsync("/dev/sign-in/as", null, ("userId", SeedIds.For("user/" + email).ToString()));
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect, $"signing in as {email}");
    }

    private string Token()
    {
        Match match = TokenPattern().Match(Html);
        match.Success.ShouldBeTrue("the current page has no antiforgery token");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public void Dispose() => client.Dispose();

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
