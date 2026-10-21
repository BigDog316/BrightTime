using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Sharing a family profile with an organisation (M3a, D44): the family picks the members and the
/// fields, the organisation's staff see exactly that while the share is live, and nobody else sees
/// anything.
/// </summary>
/// <remarks>Staff accounts 3 to 6, used by no other API test.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class ProfileShareApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (string Email, Guid Org) Staff(int index)
    {
        var staff = api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal).ElementAt(index);
        return (staff.Email, staff.Memberships.Single().OrgId);
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    private static async Task<Guid> IdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> AddYouthAsync(HttpClient owner, string first, string dob) =>
        owner.PostAsJsonAsync("api/v1/family-profile/members", new { role = "youth", firstName = first, lastName = "Okafor", dateOfBirth = dob, grade = 7, pronouns = "he/him" }, Ct);

    [Fact]
    public async Task An_organisation_sees_only_the_members_and_fields_shared_and_only_while_live()
    {
        RequireDatabase();
        (string ownerEmail, _) = Staff(3);
        (string readerEmail, Guid org) = Staff(4);
        (string strangerEmail, Guid otherOrg) = Staff(5);
        api.Data.Organisations.Single(o => o.Id == org).Status.ShouldBe("approved");
        using HttpClient owner = await SignedInAsync(ownerEmail);
        using HttpClient reader = await SignedInAsync(readerEmail);
        using HttpClient stranger = await SignedInAsync(strangerEmail);

        (await owner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 4, primaryLanguage = "yo" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid chidi = await IdAsync(await AddYouthAsync(owner, "Chidi", "2013-02-09"));
        await IdAsync(await AddYouthAsync(owner, "Ngozi", "2016-11-30"));
        await IdAsync(await owner.PostAsJsonAsync("api/v1/family-profile/contacts", new { name = "Ada Okafor", relationship = "aunt", phone = "914-555-0123" }, Ct));
        (await owner.PutAsJsonAsync($"api/v1/family-profile/members/{chidi}/support-needs",
            new { needs = new[] { new { kind = "allergy", detail = "Tree nuts", shareWithProviders = true } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid listing = api.Data.Listings.First(l => l.OrgId == org).Id;

        HttpResponseMessage granted = await owner.PostAsJsonAsync("api/v1/family-profile/shares", new
        {
            organisationId = org,
            listingId = listing,
            memberIds = new[] { chidi },
            fields = new[] { "member_names", "member_birth_dates", "support_needs" },
        }, Ct);
        Guid share = await IdAsync(granted);

        // The organisation's staff see Chidi's names and birth date, and nothing else: not his
        // sibling, not his grade, not the household, not the contacts, not his support needs.
        JsonElement inbox = await reader.GetFromJsonAsync<JsonElement>($"api/v1/org/{org}/family-shares", Ct);
        JsonElement seen = inbox.EnumerateArray().Single();
        seen.GetProperty("shareId").GetGuid().ShouldBe(share);
        seen.GetProperty("listingId").GetGuid().ShouldBe(listing);
        JsonElement one = await reader.GetFromJsonAsync<JsonElement>($"api/v1/org/{org}/family-shares/{share}", Ct);
        JsonElement member = one.GetProperty("members").EnumerateArray().Single();
        member.GetProperty("firstName").GetString().ShouldBe("Chidi");
        member.GetProperty("dateOfBirth").GetString().ShouldBe("2013-02-09");
        member.GetProperty("grade").ValueKind.ShouldBe(JsonValueKind.Null);
        member.GetProperty("pronouns").ValueKind.ShouldBe(JsonValueKind.Null);
        member.TryGetProperty("supportNeeds", out _).ShouldBeFalse();
        one.GetProperty("household").ValueKind.ShouldBe(JsonValueKind.Null);
        one.GetProperty("contacts").ValueKind.ShouldBe(JsonValueKind.Null);
        one.GetRawText().ShouldNotContain("Ngozi");
        one.GetRawText().ShouldNotContain("Tree nuts");

        // Another organisation's staff get nothing: not this organisation's inbox, not the share
        // through their own.
        HttpResponseMessage forbidden = await stranger.GetAsync(new Uri($"api/v1/org/{org}/family-shares", UriKind.Relative), Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemAsync(forbidden)).ShouldBe("urn:brighttime:problem:family.share_forbidden");
        (await stranger.GetAsync(new Uri($"api/v1/org/{otherOrg}/family-shares/{share}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.GetAsync(new Uri($"api/v1/org/{org}/family-shares", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Revoked, it is gone at once.
        (await owner.DeleteAsync(new Uri($"api/v1/family-profile/shares/{share}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        HttpResponseMessage ended = await reader.GetAsync(new Uri($"api/v1/org/{org}/family-shares/{share}", UriKind.Relative), Ct);
        ended.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemAsync(ended)).ShouldBe("urn:brighttime:problem:family.share_ended");
        (await reader.GetFromJsonAsync<JsonElement>($"api/v1/org/{org}/family-shares", Ct)).GetArrayLength().ShouldBe(0);

        // Each read names the organisation; each refusal says why.
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.family_share.read' and outcome = 'allow' and actor_org_id = @o and detail->>'share' = @s",
            ("o", org), ("s", share.ToString()))).ShouldBe(1);
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.family_share.read' and outcome = 'deny' and reason = 'family.share_forbidden' and actor_org_id = @o",
            ("o", org))).ShouldBe(2);
    }

    [Fact]
    public async Task A_grant_is_checked_and_a_second_grant_to_the_same_place_replaces_the_first()
    {
        RequireDatabase();
        (string ownerEmail, _) = Staff(6);
        using HttpClient owner = await SignedInAsync(ownerEmail);
        Guid org = api.Data.Organisations.First(o => o.Status == "approved").Id;
        Guid unapproved = api.Data.Organisations.First(o => o.Status != "approved").Id;
        Guid elsewhere = api.Data.Listings.First(l => l.OrgId != org).Id;

        HttpResponseMessage early = await owner.PostAsJsonAsync("api/v1/family-profile/shares", new { organisationId = org, memberIds = new[] { Guid.NewGuid() }, fields = new[] { "household" } }, Ct);
        early.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        (await owner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid kid = await IdAsync(await AddYouthAsync(owner, "Emeka", "2012-08-01"));

        async Task RefusedAsync(object body, string code)
        {
            HttpResponseMessage refused = await owner.PostAsJsonAsync("api/v1/family-profile/shares", body, Ct);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, code);
            (await ProblemAsync(refused)).ShouldBe("urn:brighttime:problem:" + code);
        }

        await RefusedAsync(new { organisationId = org, memberIds = new[] { kid }, fields = new[] { "home_address" } }, "family.share_fields");
        await RefusedAsync(new { organisationId = org, memberIds = Array.Empty<Guid>(), fields = new[] { "household" } }, "family.share_members");
        await RefusedAsync(new { organisationId = org, memberIds = new[] { Guid.NewGuid() }, fields = new[] { "household" } }, "family.share_members");
        await RefusedAsync(new { organisationId = unapproved, memberIds = new[] { kid }, fields = new[] { "household" } }, "family.share_org");
        await RefusedAsync(new { organisationId = org, listingId = elsewhere, memberIds = new[] { kid }, fields = new[] { "household" } }, "family.share_listing");
        await RefusedAsync(new { organisationId = org, memberIds = new[] { kid }, fields = new[] { "household" }, expiresAt = DateTimeOffset.UtcNow.AddDays(-1) }, "family.share_expiry");

        Guid first = await IdAsync(await owner.PostAsJsonAsync("api/v1/family-profile/shares",
            new { organisationId = org, memberIds = new[] { kid }, fields = new[] { "household" } }, Ct));
        Guid second = await IdAsync(await owner.PostAsJsonAsync("api/v1/family-profile/shares",
            new { organisationId = org, memberIds = new[] { kid }, fields = new[] { "household", "contacts" }, expiresAt = DateTimeOffset.UtcNow.AddMonths(6) }, Ct));
        second.ShouldNotBe(first);

        JsonElement mine = await owner.GetFromJsonAsync<JsonElement>("api/v1/family-profile/shares", Ct);
        mine.GetArrayLength().ShouldBe(2);
        JsonElement latest = mine[0];
        latest.GetProperty("id").GetGuid().ShouldBe(second);
        latest.GetProperty("status").GetString().ShouldBe("active");
        latest.GetProperty("organisationName").GetString().ShouldNotBeNullOrEmpty();
        latest.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ShouldBe(["contacts", "household"]);
        mine[1].GetProperty("status").GetString().ShouldBe("revoked");

        (await owner.DeleteAsync(new Uri($"api/v1/family-profile/shares/{first}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await api.ScalarAsync<long>("select count(*) from enrollment.profile_share where org_id = @o and member_ids = array[@k] and revoked_at is null",
            ("o", org), ("k", kid))).ShouldBe(1);
    }
}
