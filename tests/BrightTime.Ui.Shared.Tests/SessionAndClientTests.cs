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
