using System.Net;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;
using BrightTime.Ui.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>The app's session and API client: tokens, refresh, problems and the device stores.</summary>
public sealed class SessionAndClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeApi SignedInApi(string kind) => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Post, "auth/token/refresh", FakeApi.Renewed)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .On(HttpMethod.Post, "auth/logout", _ => FakeApi.Respond(HttpStatusCode.NoContent));

    [Fact]
    public async Task An_adults_refresh_token_is_kept_and_a_young_persons_never_is()
    {
        using FakeApi adultApi = SignedInApi("adult");
        await using ServiceProvider adult = adultApi.Services();
        AppSession session = adult.GetRequiredService<AppSession>();
        (await session.SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        (await adult.GetRequiredService<ISecretStore>().GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-1");
        session.Capabilities!.CanSelfEnroll.ShouldBeTrue();

        using FakeApi youthApi = SignedInApi("youth");
        await using ServiceProvider youth = youthApi.Services();
        (await youth.GetRequiredService<AppSession>().SignInAsync("teen@example.net", "pw", Ct)).ShouldBeNull();
        (await youth.GetRequiredService<ISecretStore>().GetAsync(AppSession.RefreshTokenKey)).ShouldBeNull();
        (await youth.GetRequiredService<ISecretStore>().GetAsync(AppSession.PersonKey)).ShouldBeNull();
    }

    [Fact]
    public async Task A_kept_token_signs_an_adult_back_in_and_a_dead_one_is_forgotten()
    {
        using FakeApi api = SignedInApi("adult");
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<ISecretStore>().SetAsync(AppSession.RefreshTokenKey, "refresh-0");
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        app.GetRequiredService<AppSession>().IsSignedIn.ShouldBeTrue();
        (await app.GetRequiredService<ISecretStore>().GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-2", "refresh tokens rotate");

        using FakeApi revoked = SignedInApi("adult").On(HttpMethod.Post, "auth/token/refresh", _ => FakeApi.Problem(HttpStatusCode.BadRequest, "invalid-grant"));
        await using ServiceProvider again = revoked.Services();
        await again.GetRequiredService<ISecretStore>().SetAsync(AppSession.RefreshTokenKey, "refresh-0");
        await again.GetRequiredService<AppState>().StartAsync(Ct);
        again.GetRequiredService<AppSession>().IsSignedIn.ShouldBeFalse();
        (await again.GetRequiredService<ISecretStore>().GetAsync(AppSession.RefreshTokenKey)).ShouldBeNull();
    }

    [Fact]
    public async Task Starting_offline_keeps_an_adult_signed_in_until_the_server_answers()
    {
        using FakeApi api = SignedInApi("adult");
        await using ServiceProvider first = api.Services();
        await first.GetRequiredService<AppState>().StartAsync(Ct);
        (await first.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        ISecretStore secrets = first.GetRequiredService<ISecretStore>();
        IOfflineStore offline = first.GetRequiredService<IOfflineStore>();

        // Opened again with no signal: the kept person stands in, and nothing kept is forgotten.
        api.Offline = true;
        await using ServiceProvider later = api.Services(s => s.AddSingleton(secrets).AddSingleton(offline));
        await later.GetRequiredService<AppState>().StartAsync(Ct);
        AppSession session = later.GetRequiredService<AppSession>();
        session.IsSignedIn.ShouldBeTrue();
        session.Me!.UserName.ShouldBe("sam@example.net");
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-1");
        (await later.GetRequiredService<BrightTimeApi>().SavedAsync(Ct)).Problem.ShouldBe(ApiProblem.Offline);

        // The signal returns: the next request refreshes and reads the person afresh.
        api.Offline = false;
        api.Json(HttpMethod.Get, "me/saved-items", "[]");
        int reads = api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal));
        (await later.GetRequiredService<BrightTimeApi>().SavedAsync(Ct)).IsSuccess.ShouldBeTrue();
        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)).ShouldBe(reads + 1);
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-2");

        // Signing out forgets the kept person too.
        await session.SignOutAsync(Ct);
        (await secrets.GetAsync(AppSession.PersonKey)).ShouldBeNull();
    }

    [Fact]
    public async Task Losing_the_signal_after_the_refresh_keeps_the_renewed_token_and_the_kept_person()
    {
        using FakeApi api = SignedInApi("adult");
        await using ServiceProvider first = api.Services();
        await first.GetRequiredService<AppState>().StartAsync(Ct);
        (await first.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        ISecretStore secrets = first.GetRequiredService<ISecretStore>();
        IOfflineStore offline = first.GetRequiredService<IOfflineStore>();

        // Opened again: the refresh answers (refresh-1 is spent), then the signal drops before "me".
        api.On(HttpMethod.Get, "me", _ => throw new HttpRequestException("No route to host."));
        await using ServiceProvider later = api.Services(s => s.AddSingleton(secrets).AddSingleton(offline));
        await later.GetRequiredService<AppState>().StartAsync(Ct);
        AppSession session = later.GetRequiredService<AppSession>();
        session.IsSignedIn.ShouldBeTrue();
        session.Me!.UserName.ShouldBe("sam@example.net");
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-2", "the one kept must be the one the server will still take");
        (await secrets.GetAsync(AppSession.PersonKey)).ShouldNotBeNull();

        // The signal returns: the next request reads the person afresh on the token it already has.
        api.Json(HttpMethod.Get, "me", FakeApi.Me("adult")).Json(HttpMethod.Get, "me/saved-items", "[]");
        (await later.GetRequiredService<BrightTimeApi>().SavedAsync(Ct)).IsSuccess.ShouldBeTrue();
        session.Capabilities.ShouldNotBeNull();
        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("auth/token/refresh", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Only_the_servers_refusal_ends_a_restored_session()
    {
        // A 401 for the token the refresh has just issued: the session is over.
        using FakeApi refused = SignedInApi("adult").On(HttpMethod.Get, "me", _ => FakeApi.Respond(HttpStatusCode.Unauthorized));
        await using ServiceProvider app = refused.Services();
        ISecretStore secrets = app.GetRequiredService<ISecretStore>();
        await secrets.SetAsync(AppSession.RefreshTokenKey, "refresh-0");
        await secrets.SetAsync(AppSession.PersonKey, FakeApi.Me("adult"));
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        app.GetRequiredService<AppSession>().IsSignedIn.ShouldBeFalse();
        (await secrets.GetAsync(AppSession.RefreshTokenKey)).ShouldBeNull();
        (await secrets.GetAsync(AppSession.PersonKey)).ShouldBeNull();

        // A server error on the refresh says nothing about the token: kept, and the kept person stands in.
        using FakeApi failing = SignedInApi("adult").On(HttpMethod.Post, "auth/token/refresh", _ => FakeApi.Problem(HttpStatusCode.ServiceUnavailable, "unavailable"));
        await using ServiceProvider again = failing.Services();
        ISecretStore kept = again.GetRequiredService<ISecretStore>();
        await kept.SetAsync(AppSession.RefreshTokenKey, "refresh-0");
        await kept.SetAsync(AppSession.PersonKey, FakeApi.Me("adult"));
        await again.GetRequiredService<AppState>().StartAsync(Ct);
        again.GetRequiredService<AppSession>().IsSignedIn.ShouldBeTrue();
        (await kept.GetAsync(AppSession.RefreshTokenKey)).ShouldBe("refresh-0");
    }

    [Fact]
    public async Task A_401_refreshes_once_and_retries_with_the_new_token()
    {
        int saved = 0;
        using FakeApi api = SignedInApi("adult").On(HttpMethod.Get, "me/saved-items", r =>
        {
            saved++;
            return r.Headers.Authorization?.Parameter == "access-2" ? FakeApi.Respond(HttpStatusCode.OK, "[]") : FakeApi.Respond(HttpStatusCode.Unauthorized);
        });
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct);

        ApiResult<IReadOnlyList<SavedItem>> result = await app.GetRequiredService<BrightTimeApi>().SavedAsync(Ct);

        result.IsSuccess.ShouldBeTrue();
        saved.ShouldBe(2);
        api.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("auth/token/refresh", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Problems_keep_their_code_offline_is_its_own_and_every_request_says_the_language()
    {
        using FakeApi api = new FakeApi()
            .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
            .On(HttpMethod.Post, "auth/register", _ => FakeApi.Problem(HttpStatusCode.Forbidden, "register.under_13"));
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        app.GetRequiredService<AppState>().ChooseLocale("es");
        BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();

        ApiResult refused = await client.RegisterAsync("kid@example.net", "2016-04", Ct);
        refused.Problem!.Code.ShouldBe("register.under_13");
        refused.Problem.Status.ShouldBe(403);
        api.Requests[^1].Headers.AcceptLanguage.ToString().ShouldBe("es");

        api.Offline = true;
        (await client.SearchAsync(new SearchFilters(), ct: Ct)).Problem.ShouldBe(ApiProblem.Offline);
    }

    [Fact]
    public async Task Offline_on_first_start_says_so_and_a_kept_region_opens_offline_later()
    {
        using FakeApi api = new FakeApi().Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant);
        api.Offline = true;
        await using ServiceProvider app = api.Services();
        AppState state = app.GetRequiredService<AppState>();
        await state.StartAsync(Ct);
        state.IsReady.ShouldBeFalse();
        state.StartProblem.ShouldBe(ApiProblem.Offline);

        api.Offline = false;
        await state.StartAsync(Ct);
        state.IsReady.ShouldBeTrue();

        // A second start, offline, opens from the kept copy.
        MemoryOfflineStore kept = (MemoryOfflineStore)app.GetRequiredService<IOfflineStore>();
        using FakeApi down = new() { Offline = true };
        await using ServiceProvider later = down.Services(s => s.AddSingleton<IOfflineStore>(kept));
        await later.GetRequiredService<AppState>().StartAsync(Ct);
        later.GetRequiredService<AppState>().IsReady.ShouldBeTrue();
        later.GetRequiredService<AppRegion>().Name.ShouldBe("Kettle County");
    }

    [Fact]
    public async Task Closing_the_passkey_prompt_says_nothing_and_sends_nothing()
    {
        using FakeApi api = SignedInApi("adult")
            .Json(HttpMethod.Post, "auth/passkeys/assert/options", """{"options":{"challenge":"abc"},"state":"sealed"}""");
        await using ServiceProvider app = api.Services();

        ApiProblem? outcome = await app.GetRequiredService<AppSession>().SignInWithPasskeyAsync(new DecliningAuthenticator(), Ct);

        outcome.ShouldBe(AppSession.PasskeyDeclined);
        api.Requests.ShouldNotContain(r => r.RequestUri!.AbsolutePath.EndsWith("auth/passkeys/assert", StringComparison.Ordinal));
        app.GetRequiredService<AppSession>().IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task An_external_sign_in_checks_its_state_and_sends_its_verifier()
    {
        string? sentVerifier = null;
        using FakeApi api = SignedInApi("adult").On(HttpMethod.Post, "auth/external/token", r =>
        {
            using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            sentVerifier = body.RootElement.GetProperty("verifier").GetString();
            return FakeApi.Respond(HttpStatusCode.OK, FakeApi.Tokens);
        });
        await using ServiceProvider app = api.Services();
        AppSession session = app.GetRequiredService<AppSession>();
        AppEndpoints endpoints = app.GetRequiredService<AppEndpoints>();

        // A hand-back whose state is not the one sent: refused before anything is redeemed.
        (await session.SignInExternallyAsync(new Browser(_ => new Dictionary<string, string> { ["code"] = "c", ["state"] = "forged" }), endpoints, "google", Ct))!
            .Code.ShouldBe("external.failed");
        sentVerifier.ShouldBeNull();

        // The provider's account is not connected: said as such.
        (await session.SignInExternallyAsync(new Browser(start => new Dictionary<string, string> { ["error"] = "unconnected", ["state"] = StateOf(start) }), endpoints, "google", Ct))!
            .Code.ShouldBe("external.unconnected");

        // The real hand-back: the verifier whose S256 is the challenge the browser was started with.
        string? challenge = null;
        (await session.SignInExternallyAsync(new Browser(start =>
        {
            challenge = System.Web.HttpUtility.ParseQueryString(start.Query)["challenge"];
            start.AbsolutePath.ShouldBe("/sign-in/external/app/google");
            return new Dictionary<string, string> { ["code"] = "c", ["state"] = StateOf(start) };
        }), endpoints, "google", Ct)).ShouldBeNull();
        session.IsSignedIn.ShouldBeTrue();
        string s256 = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(sentVerifier!))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        s256.ShouldBe(challenge);
    }

    private static string StateOf(Uri start) => System.Web.HttpUtility.ParseQueryString(start.Query)["state"]!;

    private sealed class Browser(Func<Uri, Dictionary<string, string>> answer) : IExternalAuthenticator
    {
        public bool IsSupported => true;

        public Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri start, Uri callback, CancellationToken cancellationToken = default)
        {
            (callback.Scheme, callback.Host).ShouldBe(("org.brighttime.app", "external"));
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(answer(start));
        }
    }

    private sealed class DecliningAuthenticator : IPasskeyAuthenticator
    {
        public bool IsSupported => true;

        public Task<PasskeyOutcome> CreateAsync(string optionsJson, CancellationToken cancellationToken = default) => Task.FromResult(PasskeyOutcome.Declined);

        public Task<PasskeyOutcome> GetAsync(string optionsJson, CancellationToken cancellationToken = default)
        {
            optionsJson.ShouldContain("\"challenge\":\"abc\"", Case.Sensitive, "the server's options reach the prompt as they were sent");
            return Task.FromResult(PasskeyOutcome.Declined);
        }
    }

    [Fact]
    public async Task The_device_language_is_used_when_the_region_offers_it()
    {
        using FakeApi api = new FakeApi().Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant);
        await using ServiceProvider app = api.Services();
        AppRegion region = app.GetRequiredService<AppRegion>();
        TenantInfo info = System.Text.Json.JsonSerializer.Deserialize<TenantInfo>(FakeApi.Tenant, AppSession.Json)!;

        region.Use(info, chosen: null, deviceLanguage: "ar-EG");
        region.Locale.Code.ShouldBe("ar");
        region.Locale.Direction.ShouldBe("rtl");
        region.TimeZone.Id.ShouldBeOneOf("America/New_York", "Eastern Standard Time");

        region.Use(info, chosen: null, deviceLanguage: "bn-BD");
        region.Locale.Code.ShouldBe("en", "Bengali is not offered in this region, so its default");
        region.SetLocale("bn").ShouldBeFalse();
        region.SetLocale("es").ShouldBeTrue();
    }
}
