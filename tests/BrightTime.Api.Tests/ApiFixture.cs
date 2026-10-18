using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using BrightTime.Infrastructure.Security;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace BrightTime.Api.Tests;

/// <summary>
/// The API host against a seeded database of its own (<c>brighttime_apitest</c>), addressed as
/// the Kettle tenant's host, with test-only signing and audit keys.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public const string DatabaseName = "brighttime_apitest";
    public const string Host = "kettle.localhost";
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
        Data = await SeedDatabase.RecreateAsync(Connection, SeedDatabase.FindSchemaDirectory(AppContext.BaseDirectory), SystemClock.Instance.TodayIn(zone), null, ct);

        string As(string login) => new NpgsqlConnectionStringBuilder(Connection) { Username = login, Password = Password }.ConnectionString;
        _root = new WebApplicationFactory<Program>();
        _factory = _root.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.UseSetting("ConnectionStrings:App", As("bt_web_login"));
            b.UseSetting("ConnectionStrings:Auth", As("bt_auth_login"));
            b.UseSetting("Tenancy:Mode", "Host");
            b.UseSetting("Tenancy:PublicScheme", "https");
            b.UseSetting("Audit:ClientHashKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Auth:Tokens:SigningKeys:0:Id", "test-1");
            b.UseSetting("Auth:Tokens:SigningKeys:0:Key", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            b.UseSetting("Auth:SignInAttemptsPerMinute", "1000");
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

    /// <summary>A client addressed to a tenant's host, the way a phone reaches it.</summary>
    public HttpClient Client(string host = Host) =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{host}/"), AllowAutoRedirect = false });

    /// <summary>Gives a seeded account a fresh password and returns it. Never printed.</summary>
    public async Task<string> SetPasswordAsync(string email)
    {
        string password = "Api-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        await ExecuteAsync("update identity.app_user set password_hash = @h, access_failed_count = 0, lockout_end = null where normalized_email = upper(@e)",
            ("h", Argon2PasswordHasher.HashPassword(password)), ("e", email));
        return password;
    }

    public async Task<TokenPair> SignInAsync(string email, string password, string host = Host)
    {
        using HttpClient client = Client(host);
        HttpResponseMessage response = await client.PostAsJsonAsync("api/v1/auth/token", new { email, password }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>(TestContext.Current.CancellationToken))!;
    }

    public static HttpClient WithBearer(HttpClient client, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test setup written as literals; values are bound parameters.")]
    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        await using NpgsqlConnection connection = new(Connection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

public sealed record TokenPair(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

[CollectionDefinition(Name)]
public sealed class ApiDatabase : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
