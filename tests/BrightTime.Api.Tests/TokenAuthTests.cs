using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// The mobile app's sign-in through the real pipeline: tenant by host, password for tokens,
/// rotation with reuse detection, sign out, and the principal rebuilt on every request.
/// Each test signs in as an account no other test in this project uses.
/// </summary>
[Collection(ApiDatabase.Name)]
public sealed class TokenAuthTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task A_password_signs_in_for_tokens_and_the_access_token_reads_me()
    {
        RequireDatabase();
        string email = Admin(0);
        string password = await api.SetPasswordAsync(email);

        TokenPair tokens = await api.SignInAsync(email, password);

        tokens.TokenType.ShouldBe("Bearer");
        tokens.ExpiresIn.ShouldBe(600);
        using HttpClient client = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
        HttpResponseMessage me = await client.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct);
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("userName").GetString().ShouldBe(email);
        body.GetProperty("accountKind").GetString().ShouldBe("adult");
        body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldContain("provider_admin");
        body.GetProperty("organisations").GetArrayLength().ShouldBe(1);

        Guid user = SeedIds.For("user/" + email);
        (await api.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'auth.sign_in' and outcome = 'allow' and subject_ids = array[@u] and detail->>'channel' = 'api'",
            ("u", user))).ShouldBe(1);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_get_the_same_answer()
    {
        RequireDatabase();
        string email = Admin(1);
        await api.SetPasswordAsync(email);
        using HttpClient client = api.Client();

        HttpResponseMessage wrong = await client.PostAsJsonAsync("api/v1/auth/token", new { email, password = "not-the-password-at-all" }, Ct);
        HttpResponseMessage unknown = await client.PostAsJsonAsync("api/v1/auth/token", new { email = "nobody-here@example.org", password = "whatever-it-is-1" }, Ct);

        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement a = await wrong.Content.ReadFromJsonAsync<JsonElement>(Ct);
        JsonElement b = await unknown.Content.ReadFromJsonAsync<JsonElement>(Ct);
        a.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:invalid-grant");
        a.GetProperty("title").GetString().ShouldBe(b.GetProperty("title").GetString());
        wrong.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_refresh_rotates_and_presenting_a_spent_token_ends_the_whole_family()
    {
        RequireDatabase();
        string email = Admin(2);
        TokenPair first = await api.SignInAsync(email, await api.SetPasswordAsync(email));
        using HttpClient client = api.Client();

        HttpResponseMessage rotated = await client.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = first.RefreshToken }, Ct);
        rotated.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenPair second = (await rotated.Content.ReadFromJsonAsync<TokenPair>(Ct))!;
        second.RefreshToken.ShouldNotBe(first.RefreshToken);

        // The first token again: a copy is in someone else's hands.
        (await client.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = first.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = second.RefreshToken }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "the newer token dies with its family");

        Guid user = SeedIds.For("user/" + email);
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'auth.token.reused' and outcome = 'deny' and subject_ids = array[@u]", ("u", user)))
            .ShouldBe(1);
        (await api.ScalarAsync<long>("select count(*) from identity.refresh_token where user_id = @u and revoked_at is null", ("u", user))).ShouldBe(0);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_says_nothing_about_tokens_it_does_not_know()
    {
        RequireDatabase();
        string email = Admin(3);
        TokenPair tokens = await api.SignInAsync(email, await api.SetPasswordAsync(email));
        using HttpClient client = api.Client();

        (await client.PostAsJsonAsync("api/v1/auth/logout", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("api/v1/auth/logout", new { refreshToken = "not-a-token" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Signing_out_everywhere_stops_access_tokens_at_their_next_request()
    {
        RequireDatabase();
        string email = Admin(4);
        TokenPair tokens = await api.SignInAsync(email, await api.SetPasswordAsync(email));
        using HttpClient client = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);

        (await client.DeleteAsync(new Uri("api/v1/me/sessions", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "the stamp has changed");
        using HttpClient anonymous = api.Client();
        (await anonymous.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_suspended_account_loses_its_access_token_at_once()
    {
        RequireDatabase();
        string email = Admin(5);
        TokenPair tokens = await api.SignInAsync(email, await api.SetPasswordAsync(email));
        await api.ExecuteAsync("update identity.app_user set status = 'suspended' where normalized_email = upper(@e)", ("e", email));

        using HttpClient client = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
        (await client.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using HttpClient anonymous = api.Client();
        (await anonymous.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_token_is_refused_by_another_tenant_and_a_tampered_or_missing_one_by_everyone()
    {
        RequireDatabase();
        string email = Admin(6);
        TokenPair tokens = await api.SignInAsync(email, await api.SetPasswordAsync(email));

        Guid other = UuidV7.NewId();
        string otherHost = $"other-{other.ToString("N")[..8]}.localhost";
        await api.ExecuteAsync(
            """
            insert into platform.tenant (id, slug, display_name, region_name, region_kind, default_locale, timezone, status)
            values (@t, @s, 'Other', 'Elsewhere', 'demo', 'en', 'America/New_York', 'active');
            insert into platform.tenant_domain (tenant_id, host, is_primary) values (@t, @h, true);
            """, ("t", other), ("s", "other-" + other.ToString("N")), ("h", otherHost));

        using (HttpClient elsewhere = ApiFixture.WithBearer(api.Client(otherHost), tokens.AccessToken))
        {
            (await elsewhere.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        string tampered = tokens.AccessToken[..^4] + (tokens.AccessToken.EndsWith("AAAA", StringComparison.Ordinal) ? "BBBB" : "AAAA");
        using (HttpClient forged = ApiFixture.WithBearer(api.Client(), tampered))
        {
            (await forged.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using HttpClient none = api.Client();
        (await none.GetAsync(new Uri("api/v1/me", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_youths_session_lapses_after_half_an_hour_unused_and_an_adults_after_a_month()
    {
        RequireDatabase();
        string adult = Admin(7);
        TokenPair adultTokens = await api.SignInAsync(adult, await api.SetPasswordAsync(adult));

        string youth = $"teen-{Guid.NewGuid():N}@example.org";
        await api.ExecuteAsync(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email,
                                           security_stamp, concurrency_stamp, account_kind, age_band)
            values (@id, @t, @e, upper(@e), @e, upper(@e), 'stamp', 'concurrency', 'youth', '15_17')
            """, ("id", UuidV7.NewId()), ("t", KettleSeed.TenantId), ("e", youth));
        TokenPair youthTokens = await api.SignInAsync(youth, await api.SetPasswordAsync(youth));

        (youthTokens.RefreshTokenExpiresAt - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        (adultTokens.RefreshTokenExpiresAt - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromDays(29.9), TimeSpan.FromDays(30.1));
    }

    [Fact]
    public async Task A_password_reset_ends_every_api_session()
    {
        RequireDatabase();
        string email = Admin(8);
        TokenPair tokens = await api.SignInAsync(email, await api.SetPasswordAsync(email));

        await using (AsyncServiceScope scope = api.Factory.Services.CreateAsyncScope())
        {
            PasswordAccounts passwords = scope.ServiceProvider.GetRequiredService<PasswordAccounts>();
            TenantId tenant = new(KettleSeed.TenantId);
            PasswordResetRequest reset = (await passwords.CreateResetAsync(tenant, email, Ct))!;
            (await passwords.ResetAsync(tenant, reset.Token, "A-new-passphrase-" + Guid.NewGuid().ToString("N")[..8], Ct)).IsSuccess.ShouldBeTrue();
        }

        using HttpClient client = api.Client();
        (await client.PostAsJsonAsync("api/v1/auth/token/refresh", new { refreshToken = tokens.RefreshToken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await api.ScalarAsync<string>("select revoked_reason from identity.refresh_token where user_id = @u", ("u", SeedIds.For("user/" + email))))
            .ShouldBe("password_reset");
    }

    [Fact]
    public async Task An_unknown_host_is_no_tenant_and_gets_nothing()
    {
        RequireDatabase();
        using HttpClient client = api.Client("nobody.localhost");

        (await client.PostAsJsonAsync("api/v1/auth/token", new { email = "a@example.org", password = "whatever-1" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
