using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrightTime.Api.Auth;
using BrightTime.Hosting.Auth;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Web.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// The terms of use (D37) and passkeys from the app (D38), through the API. Passkeys are made and
/// used by a software authenticator, so the server's WebAuthn checks run for real.
/// </summary>
/// <remarks>Admins 55 to 57; the young person is registered fresh.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class TermsAndPasskeyApiTests(ApiFixture api)
{
    private const string Origin = "https://kettle.localhost";
    private const string RpId = "kettle.localhost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    [Fact]
    public async Task A_young_person_agrees_to_the_current_terms_and_may_then_sign_up_for_programs_themselves()
    {
        RequireDatabase();
        using HttpClient anyone = api.Client();
        JsonElement terms = await anyone.GetFromJsonAsync<JsonElement>("api/v1/terms", Ct);
        string hash = terms.GetProperty("hash").GetString()!;
        hash.Length.ShouldBe(64);
        terms.GetProperty("text").GetString()!.ShouldContain("We never keep your home address");

        string teen = $"teen-{Guid.NewGuid():N}@example.net";
        (await anyone.PostAsJsonAsync("api/v1/auth/register", new { email = teen, birthMonth = DateTime.UtcNow.AddYears(-15).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await api.Mail.WaitForAsync(teen);
        using HttpClient me = await SignedInAsync(teen);
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/capabilities", Ct)).GetProperty("canSelfEnroll").GetBoolean().ShouldBeFalse();
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/terms", Ct)).GetProperty("acceptedAt").ValueKind.ShouldBe(JsonValueKind.Null);

        HttpResponseMessage stale = await me.PostAsJsonAsync("api/v1/me/terms", new { hash = new string('0', 64) }, Ct);
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(stale)).ShouldBe("urn:brighttime:problem:terms.stale");

        HttpResponseMessage agreed = await me.PostAsJsonAsync("api/v1/me/terms", new { hash }, Ct);
        agreed.StatusCode.ShouldBe(HttpStatusCode.OK);
        DateTimeOffset at = (await agreed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("acceptedAt").GetDateTimeOffset();

        // Recomputed in the same request: the young person may sign up for programs themselves now.
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/capabilities", Ct)).GetProperty("canSelfEnroll").GetBoolean().ShouldBeTrue();

        // Agreeing again changes nothing; the record names the version and the words' hash.
        (await (await me.PostAsJsonAsync("api/v1/me/terms", new { hash }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct))
            .GetProperty("acceptedAt").GetDateTimeOffset().ShouldBe(at);
        (await api.ScalarAsync<long>(
            "select count(*) from identity.consent_record r join identity.app_user u on u.id = r.user_id where u.normalized_email = upper(@e) and r.document_kind = 'terms' and encode(r.document_hash, 'hex') = @h",
            ("e", teen), ("h", hash))).ShouldBe(1);
    }

    [Fact]
    public async Task A_passkey_added_in_the_app_signs_in_for_tokens_and_a_forged_one_does_not()
    {
        RequireDatabase();
        string email = Admin(55);
        using HttpClient me = await SignedInAsync(email);
        using SoftwarePasskey key = new(Origin, RpId);

        HttpResponseMessage started = await me.PostAsync(new Uri("api/v1/auth/passkeys/options", UriKind.Relative), null, Ct);
        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode ceremony = (await started.Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        ceremony["options"]!["rp"]!["id"]!.GetValue<string>().ShouldBe(RpId);
        HttpResponseMessage added = await me.PostAsJsonAsync("api/v1/auth/passkeys",
            new JsonObject { ["state"] = ceremony["state"]!.GetValue<string>(), ["credential"] = key.Create(ceremony["options"]!), ["name"] = "Test phone" }, Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement listed = (await me.GetFromJsonAsync<JsonElement>("api/v1/me/passkeys", Ct)).EnumerateArray().Single();
        listed.GetProperty("name").GetString().ShouldBe("Test phone");

        // Signed out, the same passkey signs in: tokens, as a password would give.
        using HttpClient app = api.Client();
        JsonNode assertion = (await (await app.PostAsync(new Uri("api/v1/auth/passkeys/assert/options", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        HttpResponseMessage signedIn = await app.PostAsJsonAsync("api/v1/auth/passkeys/assert",
            new JsonObject { ["state"] = assertion["state"]!.GetValue<string>(), ["credential"] = key.Get(assertion["options"]!) }, Ct);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenPair tokens = (await signedIn.Content.ReadFromJsonAsync<TokenPair>(Ct))!;
        using HttpClient viaPasskey = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
        (await viaPasskey.GetFromJsonAsync<JsonElement>("api/v1/me", Ct)).GetProperty("id").GetGuid().ShouldBe(SeedIds.For("user/" + email));

        // A tampered signature, an origin that is not this region's, or replayed state: the same refusal.
        JsonNode again = (await (await app.PostAsync(new Uri("api/v1/auth/passkeys/assert/options", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        foreach (JsonObject forged in new[] { key.Get(again["options"]!, tamper: true), key.Get(again["options"]!, asOrigin: "https://evil.example") })
        {
            HttpResponseMessage refused = await app.PostAsJsonAsync("api/v1/auth/passkeys/assert",
                new JsonObject { ["state"] = again["state"]!.GetValue<string>(), ["credential"] = forged }, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemTypeAsync(refused)).ShouldBe(TokenEndpoints.InvalidGrantType);
        }

        HttpResponseMessage noState = await app.PostAsJsonAsync("api/v1/auth/passkeys/assert",
            new JsonObject { ["state"] = "not-sealed", ["credential"] = key.Get(again["options"]!) }, Ct);
        (await ProblemTypeAsync(noState)).ShouldBe(TokenEndpoints.InvalidGrantType);

        // Removing it needs the same recent sign-in; afterwards it signs in no more.
        (await me.DeleteAsync(new Uri($"api/v1/me/passkeys/{listed.GetProperty("id").GetGuid()}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonNode after = (await (await app.PostAsync(new Uri("api/v1/auth/passkeys/assert/options", UriKind.Relative), null, Ct)).Content.ReadFromJsonAsync<JsonNode>(Ct))!;
        (await app.PostAsJsonAsync("api/v1/auth/passkeys/assert",
            new JsonObject { ["state"] = after["state"]!.GetValue<string>(), ["credential"] = key.Get(after["options"]!) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Adding_a_passkey_needs_a_recent_sign_in()
    {
        RequireDatabase();
        string email = Admin(56);
        await api.SetPasswordAsync(email);

        // A token from a sign-in an hour ago, as a long-refreshed session carries.
        string stale = await TokenAsync(SeedIds.For("user/" + email), DateTimeOffset.UtcNow.AddHours(-1));
        using HttpClient old = ApiFixture.WithBearer(api.Client(), stale);
        (await old.GetAsync(new Uri("api/v1/me/passkeys", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "listing is not a change");
        (await old.PostAsync(new Uri("api/v1/auth/passkeys/options", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<string> TokenAsync(Guid user, DateTimeOffset authenticatedAt)
    {
        await using AsyncServiceScope scope = api.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(api.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        ClaimsPrincipal principal = (await scope.ServiceProvider.GetRequiredService<BrightTimePrincipalFactory>()
            .CreateAsync(new TenantId(api.Data.TenantId), new UserId(user), new AuthenticationEvent(AuthenticationMethods.Password, authenticatedAt), Ct))!;
        return scope.ServiceProvider.GetRequiredService<AccessTokens>().Issue(principal, new Uri("https://kettle.localhost/"), DateTimeOffset.UtcNow).Token;
    }
}
