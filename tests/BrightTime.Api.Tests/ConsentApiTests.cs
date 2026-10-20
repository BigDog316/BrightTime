using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;

namespace BrightTime.Api.Tests;

/// <summary>
/// Guardians, child profiles and parental consent through the API: what M2's acceptance asks,
/// that a guardian can make a child profile, grant and revoke consent, and that a revoke takes
/// the ability away at once.
/// </summary>
/// <remarks>Each test signs in as its own seeded adult (admins from index 20 on), so tests never share a guardian.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class ConsentApiTests(ApiFixture api)
{
    private const string Organiser = "organiser@kettle.example.org";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static string RecentBirthMonth => DateTime.UtcNow.AddYears(-8).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonElement> CreateChildAsync(HttpClient guardian, string name = "Sam")
    {
        HttpResponseMessage made = await guardian.PostAsJsonAsync("api/v1/children", new { givenName = name, birthMonth = RecentBirthMonth, relationship = "parent" }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await made.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task AttestAsync(Guid linkId)
    {
        using HttpClient organiser = await SignedInAsync(Organiser);
        (await organiser.PostAsync(new Uri($"api/v1/organiser/guardian-links/{linkId}/attest", UriKind.Relative), null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> GrantAsync(HttpClient guardian, Guid child, ConsentScope scope, string? hash = null) =>
        guardian.PostAsJsonAsync($"api/v1/children/{child}/consents",
            new { scope = scope.ToDbValue(), disclosureHash = hash ?? ConsentDisclosures.For(scope).HashHex }, Ct);

    private static async Task<JsonElement> ChildAsync(HttpClient guardian, Guid child) =>
        await guardian.GetFromJsonAsync<JsonElement>($"api/v1/children/{child}", Ct);

    [Fact]
    public async Task A_new_child_profile_can_do_nothing_until_its_guardian_is_verified()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(20));

        JsonElement child = await CreateChildAsync(guardian, "Ari");

        child.GetProperty("givenName").GetString().ShouldBe("Ari");
        child.GetProperty("status").GetString().ShouldBe("pending_consent");
        child.GetProperty("link").GetProperty("status").GetString().ShouldBe("pending");
        JsonElement can = child.GetProperty("capabilities");
        can.GetProperty("isUnder13").GetBoolean().ShouldBeTrue();
        can.EnumerateObject().Where(p => p.Name != "isUnder13").ShouldAllBe(p => p.Value.ValueKind == JsonValueKind.False);

        // A pending guardian cannot consent to anything yet.
        Guid id = child.GetProperty("id").GetGuid();
        (await GrantAsync(guardian, id, ConsentScope.MediaCapture)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and subject_ids = array[@c]",
            ("a", Guardianship.ChildCreated), ("c", id))).ShouldBe(1);

        // Age moves on its own: the day the band next changes, a recompute is already booked.
        (await api.ScalarAsync<long>("select count(*) from platform.job where unique_key like @k and state = 'ready' and run_after > now()",
            ("k", $"{RecomputeCapabilitiesHandler.JobType}:{id:N}:%"))).ShouldBe(1);
    }

    [Fact]
    public async Task A_signed_form_verifies_the_guardian_and_a_revoke_takes_the_ability_away_at_once()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(21));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();

        await AttestAsync(made.GetProperty("link").GetProperty("id").GetGuid());

        JsonElement verified = await ChildAsync(guardian, id);
        verified.GetProperty("status").GetString().ShouldBe("active");
        verified.GetProperty("link").GetProperty("verificationMethod").GetString().ShouldBe(VerificationMethod.SignedFormReview);
        verified.GetProperty("consents").EnumerateArray().Select(c => c.GetProperty("scope").GetString()).ShouldBe(["account"]);
        verified.GetProperty("capabilities").GetProperty("hasAccountConsent").GetBoolean().ShouldBeTrue();
        verified.GetProperty("capabilities").GetProperty("canUploadMedia").GetBoolean().ShouldBeFalse();

        (await GrantAsync(guardian, id, ConsentScope.MediaCapture)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ChildAsync(guardian, id)).GetProperty("capabilities").GetProperty("canUploadMedia").GetBoolean().ShouldBeTrue();

        // The revoke and the capability change are one transaction; the next read sees both.
        (await guardian.DeleteAsync(new Uri($"api/v1/children/{id}/consents/media_capture", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ChildAsync(guardian, id)).GetProperty("capabilities").GetProperty("canUploadMedia").GetBoolean().ShouldBeFalse();
        (await api.ScalarAsync<bool>("select can_upload_media from identity.youth_capability where user_id = @c", ("c", id))).ShouldBeFalse();

        // Withdrawing the account consent stops the profile: the schema's own trigger agrees.
        (await guardian.DeleteAsync(new Uri($"api/v1/children/{id}/consents/account", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement stopped = await ChildAsync(guardian, id);
        stopped.GetProperty("status").GetString().ShouldBe("pending_consent");
        stopped.GetProperty("capabilities").GetProperty("hasAccountConsent").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Consent_is_to_the_exact_words_shown()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(22));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();
        await AttestAsync(made.GetProperty("link").GetProperty("id").GetGuid());

        HttpResponseMessage stale = await GrantAsync(guardian, id, ConsentScope.Enrollment, hash: new string('0', 64));

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await stale.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:consent.stale_disclosure");
        problem.GetProperty("current").GetString().ShouldBe(ConsentDisclosures.For(ConsentScope.Enrollment).HashHex);

        (await GrantAsync(guardian, id, ConsentScope.Enrollment)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await api.ScalarAsync<byte[]>("select consent_text_hash from identity.parental_consent where child_user_id = @c and scope = 'enrollment'", ("c", id)))
            .ShouldBe(ConsentDisclosures.For(ConsentScope.Enrollment).Hash);
    }

    [Fact]
    public async Task No_consent_lifts_the_under_13_floor()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(23));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();
        await AttestAsync(made.GetProperty("link").GetProperty("id").GetGuid());

        HttpResponseMessage post = await GrantAsync(guardian, id, ConsentScope.CommunityParticipation);
        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await post.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:consent.not_available_under_13");

        foreach (ConsentScope scope in ConsentDisclosures.All.Where(d => d.AvailableUnder13).Select(d => d.Scope))
        {
            (await GrantAsync(guardian, id, scope)).StatusCode.ShouldBe(HttpStatusCode.OK, scope.ToString());
        }

        JsonElement can = (await ChildAsync(guardian, id)).GetProperty("capabilities");
        can.GetProperty("canPostInCommunity").GetBoolean().ShouldBeFalse();
        can.GetProperty("canPublishShowcase").GetBoolean().ShouldBeFalse();
        can.GetProperty("canReceiveSms").GetBoolean().ShouldBeFalse();
        can.GetProperty("canBeMessagedByOrganisation").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Email_plus_cannot_stand_behind_a_consent_that_discloses()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(24));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();
        Guid link = made.GetProperty("link").GetProperty("id").GetGuid();
        await AttestAsync(link);
        // As if verified by email plus, which arrives with account mail in the next step.
        await api.ExecuteAsync("update identity.guardian_link set verification_method = 'email_plus' where id = @l", ("l", link));

        (await GrantAsync(guardian, id, ConsentScope.MediaCapture)).StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage messaging = await GrantAsync(guardian, id, ConsentScope.Messaging);
        messaging.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await messaging.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:consent.method_insufficient");
        problem.GetProperty("required").GetString().ShouldBe(VerificationMethod.SignedFormReview);
    }

    [Fact]
    public async Task Nobody_else_sees_or_changes_a_child()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(25));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();
        Guid link = made.GetProperty("link").GetProperty("id").GetGuid();
        await AttestAsync(link);

        using HttpClient stranger = await SignedInAsync(Admin(26));
        (await stranger.GetAsync(new Uri($"api/v1/children/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GrantAsync(stranger, id, ConsentScope.MediaCapture)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.DeleteAsync(new Uri($"api/v1/children/{id}/consents/account", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.PostAsync(new Uri($"api/v1/guardian-links/{link}/revoke", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetFromJsonAsync<JsonElement>("api/v1/children", Ct)).GetArrayLength().ShouldBe(0);

        // Attesting needs the organiser's permission.
        (await stranger.PostAsync(new Uri($"api/v1/organiser/guardian-links/{link}/attest", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // And nothing at all without signing in.
        using HttpClient anonymous = api.Client();
        (await anonymous.GetAsync(new Uri("api/v1/children", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_the_link_ends_every_consent_given_through_it()
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(27));
        JsonElement made = await CreateChildAsync(guardian);
        Guid id = made.GetProperty("id").GetGuid();
        Guid link = made.GetProperty("link").GetProperty("id").GetGuid();
        await AttestAsync(link);
        (await GrantAsync(guardian, id, ConsentScope.Enrollment)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await guardian.PostAsync(new Uri($"api/v1/guardian-links/{link}/revoke", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await api.ScalarAsync<long>("select count(*) from identity.parental_consent where child_user_id = @c and revoked_at is null", ("c", id))).ShouldBe(0);
        (await api.ScalarAsync<string>("select status from identity.app_user where id = @c", ("c", id))).ShouldBe("pending_consent");
        (await guardian.GetAsync(new Uri($"api/v1/children/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Sam", "2001-04", "parent", "urn:brighttime:problem:consent.not_under_13")]
    [InlineData("", "recent", "parent", "urn:brighttime:problem:consent.invalid_given_name")]
    [InlineData("Sam", "recent", "neighbour", "urn:brighttime:problem:consent.invalid_relationship")]
    [InlineData("Sam", "2099-01", "parent", "urn:brighttime:problem:consent.invalid_birth_month")]
    public async Task A_child_profile_needs_a_name_a_relationship_and_an_age_under_13(string name, string month, string relationship, string type)
    {
        RequireDatabase();
        using HttpClient guardian = await SignedInAsync(Admin(28));

        HttpResponseMessage response = await guardian.PostAsJsonAsync("api/v1/children",
            new { givenName = name, birthMonth = month == "recent" ? RecentBirthMonth : month, relationship }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe(type);
    }

    [Fact]
    public async Task An_adult_has_every_capability_and_disclosures_name_their_methods()
    {
        RequireDatabase();
        using HttpClient adult = await SignedInAsync(Admin(29));

        JsonElement can = await adult.GetFromJsonAsync<JsonElement>("api/v1/me/capabilities", Ct);
        can.EnumerateObject().Where(p => p.Name != "isUnder13").ShouldAllBe(p => p.Value.ValueKind == JsonValueKind.True);

        JsonElement disclosures = await adult.GetFromJsonAsync<JsonElement>("api/v1/consent/disclosures", Ct);
        JsonElement messaging = disclosures.EnumerateArray().Single(d => d.GetProperty("scope").GetString() == "messaging");
        messaging.GetProperty("methods").EnumerateArray().Select(m => m.GetString()).ShouldBe([VerificationMethod.SignedFormReview]);
        messaging.GetProperty("hash").GetString().ShouldBe(ConsentDisclosures.For(ConsentScope.Messaging).HashHex);
    }
}
