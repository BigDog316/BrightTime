using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// M3's first acceptance item (docs/plan/09): a family applies once and uses the profile for a
/// second programme, with explicit sharing per organisation. One profile, entered once; two
/// organisations; each sees only what was shared with it, and neither sees the other's application.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the seventh and eighth organisations with no staff (the
/// messaging, payment and report tests take the third to sixth); their admins read the queues.
/// The family is a fresh account.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class M3AcceptanceTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Programme(int nth)
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(nth).First();
        return (unstaffed.First(l => l.OrgId == org).Id, org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task<Guid> ApplyAsync(HttpClient family, Guid listing, Guid member, string[] share)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "m3-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await family.SendAsync(request, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = share }, Ct));
        return application;
    }

    [Fact]
    public async Task A_family_enters_its_profile_once_and_applies_to_two_organisations_sharing_separately_with_each()
    {
        RequireDatabase();
        (Guid firstListing, Guid firstOrg) = Programme(6);
        (Guid secondListing, Guid secondOrg) = Programme(7);
        firstOrg.ShouldNotBe(secondOrg);
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient firstAdmin = await SignedInAsync(AdminOf(firstOrg));
        using HttpClient secondAdmin = await SignedInAsync(AdminOf(secondOrg));

        // The profile, entered once.
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Nana", lastName = "Darko", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        // Two programmes at two organisations, from the same profile: names to the first, names and birth date to the second.
        Guid first = await ApplyAsync(family, firstListing, member, ["member_names"]);
        Guid second = await ApplyAsync(family, secondListing, member, ["member_names", "member_birth_dates"]);
        (await api.ScalarAsync<long>("select count(*) from enrollment.family_member m join enrollment.application a on a.family_member_id = m.id where a.id in (@a, @b)",
            ("a", first), ("b", second))).ShouldBe(2, "both applications are the one member");
        (await api.ScalarAsync<long>("select count(distinct family_member_id) from enrollment.application where id in (@a, @b)", ("a", first), ("b", second))).ShouldBe(1);

        // One share per organisation, each holding only what that organisation was given.
        JsonElement shares = await JsonAsync(await family.GetAsync(new Uri("api/v1/family-profile/shares", UriKind.Relative), Ct));
        Dictionary<Guid, string[]> byOrg = shares.EnumerateArray().ToDictionary(s => s.GetProperty("organisationId").GetGuid(),
            s => s.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).Order(StringComparer.Ordinal).ToArray());
        byOrg[firstOrg].ShouldBe(["member_names"]);
        byOrg[secondOrg].ShouldBe(["member_birth_dates", "member_names"]);

        // Each organisation sees its own application, named, and only what it was given.
        JsonElement seenFirst = await JsonAsync(await firstAdmin.GetAsync(new Uri($"api/v1/org/{firstOrg}/applications/{first}", UriKind.Relative), Ct));
        (seenFirst.GetProperty("firstName").GetString(), seenFirst.GetProperty("dateOfBirth").ValueKind).ShouldBe(("Nana", JsonValueKind.Null));
        JsonElement seenSecond = await JsonAsync(await secondAdmin.GetAsync(new Uri($"api/v1/org/{secondOrg}/applications/{second}", UriKind.Relative), Ct));
        (seenSecond.GetProperty("firstName").GetString(), seenSecond.GetProperty("dateOfBirth").GetString()).ShouldBe(("Nana", dob));

        // Neither sees the other's.
        (await firstAdmin.GetAsync(new Uri($"api/v1/org/{firstOrg}/applications/{second}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await secondAdmin.GetAsync(new Uri($"api/v1/org/{secondOrg}/applications/{first}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JsonAsync(await firstAdmin.GetAsync(new Uri($"api/v1/org/{firstOrg}/listings/{firstListing}/applications", UriKind.Relative), Ct)))
            .EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ShouldNotContain(second);

        // Withdrawing the second organisation's share takes the name back from it, and from it only.
        Guid secondShare = shares.EnumerateArray().Single(s => s.GetProperty("organisationId").GetGuid() == secondOrg).GetProperty("id").GetGuid();
        (await family.DeleteAsync(new Uri($"api/v1/family-profile/shares/{secondShare}", UriKind.Relative), Ct)).IsSuccessStatusCode.ShouldBeTrue();
        (await JsonAsync(await secondAdmin.GetAsync(new Uri($"api/v1/org/{secondOrg}/applications/{second}", UriKind.Relative), Ct)))
            .GetProperty("firstName").ValueKind.ShouldBe(JsonValueKind.Null);
        (await JsonAsync(await firstAdmin.GetAsync(new Uri($"api/v1/org/{firstOrg}/applications/{first}", UriKind.Relative), Ct)))
            .GetProperty("firstName").GetString().ShouldBe("Nana");
    }
}
