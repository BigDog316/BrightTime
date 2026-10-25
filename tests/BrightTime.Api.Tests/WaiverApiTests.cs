using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace BrightTime.Api.Tests;

/// <summary>
/// Waivers with e-signature (M3a, D47): an organisation versions its waivers and attaches them to
/// listings; a parent signs the exact text for a child's application, and a required waiver must
/// be signed before the application goes in.
/// </summary>
/// <remarks>
/// Families are staff accounts 11 and 12. The provider is the admin of a builtin-registration
/// listing's organisation that no staff-based test and no other application test uses.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class WaiverApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private IEnumerable<(string Email, Guid Org)> AllStaff() =>
        api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal)
            .Select(u => (u.Email, u.Memberships.Single().OrgId));

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    /// <summary>The second organisation's builtin listing among those no staff-based test touches (ApplicationApiTests takes the first).</summary>
    private (Guid Id, Guid Org) Listing()
    {
        HashSet<Guid> taken = [.. AllStaff().Take(13).Select(s => s.Org)];
        var candidates = api.Data.Listings.Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !taken.Contains(l.OrgId)
            && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved").ToList();
        Guid firstOrg = candidates[0].OrgId;
        var listing = candidates.First(l => l.OrgId != firstOrg);
        return (listing.Id, listing.OrgId);
    }

    /// <summary>
    /// A listing of an organisation of its own, for the test that attaches a required waiver: on the
    /// shared listing it would block the other test's submission whenever it ran first. The
    /// fourteenth organisation with no staff, past those the other classes take.
    /// </summary>
    private (Guid Id, Guid Org) OwnListing()
    {
        HashSet<Guid> staffed = [.. AllStaff().Select(s => s.Org)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(13).First();
        return unstaffed.First(l => l.OrgId == org);
    }

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

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private static async Task<Guid> ApplyAsync(HttpClient family, Guid listing, string first, string dateOfBirth)
    {
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = first, lastName = "Nwosu", dateOfBirth }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("api/v1/applications", UriKind.Relative)) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        request.Headers.Add("Idempotency-Key", "waiver-" + Guid.NewGuid().ToString("N"));
        return (await JsonAsync(await family.SendAsync(request, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static string YearsAgo(int years) => DateTime.UtcNow.AddYears(-years).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_parent_signs_the_exact_text_and_a_required_waiver_gates_submission()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Listing();
        using HttpClient provider = await SignedInAsync(AdminOf(org));
        using HttpClient family = await SignedInAsync(AllStaff().ElementAt(11).Email);

        // The provider's waivers: one required, versioned; one optional.
        JsonElement draft = await JsonAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/waivers",
            new { title = "Activity release", body = "  You understand the activity carries some risk.\r\nYou agree to the rules.  " }, Ct), HttpStatusCode.Created);
        Guid release = draft.GetProperty("id").GetGuid();
        draft.GetProperty("bodyMarkdown").GetString().ShouldBe("You understand the activity carries some risk.\nYou agree to the rules.");
        JsonElement edited = await JsonAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/waivers/{release}",
            new { body = "You understand the activity carries some risk.\nYou agree to follow the staff's directions." }, Ct));
        edited.GetProperty("hash").GetString().ShouldNotBe(draft.GetProperty("hash").GetString());
        await ProblemAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waivers/{release}", new { isRequired = true }, Ct), HttpStatusCode.Conflict, "waiver.not_published");
        await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{release}/publish", UriKind.Relative), null, Ct));
        await ProblemAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/waivers/{release}", new { body = "Changed after publishing." }, Ct), HttpStatusCode.Conflict, "waiver.not_draft");
        (await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waivers/{release}", new { isRequired = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Guid photos = (await JsonAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/waivers",
            new { title = "Photo release", body = "Photos of the program may appear in our newsletter." }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{photos}/publish", UriKind.Relative), null, Ct));
        (await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waivers/{photos}", new { isRequired = false }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The family can't submit until the required one is signed.
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        Guid application = await ApplyAsync(family, listing, "Amara", YearsAgo(10));
        await ProblemAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct),
            HttpStatusCode.BadRequest, "application.waivers_unsigned");

        JsonElement[] asked = [.. (await family.GetFromJsonAsync<JsonElement>($"api/v1/applications/{application}/waivers", Ct)).EnumerateArray()];
        asked.Length.ShouldBe(2);
        JsonElement toSign = asked.Single(w => w.GetProperty("waiverId").GetGuid() == release);
        toSign.GetProperty("isRequired").GetBoolean().ShouldBeTrue();
        toSign.GetProperty("signedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        string hash = toSign.GetProperty("hash").GetString()!;
        hash.ShouldBe(edited.GetProperty("hash").GetString());

        string sign = $"api/v1/applications/{application}/waivers/{release}/sign";
        await ProblemAsync(await family.PostAsJsonAsync(sign, new { typedName = "Ngozi Nwosu", hash = draft.GetProperty("hash").GetString() }, Ct), HttpStatusCode.Conflict, "waiver.changed");
        await ProblemAsync(await family.PostAsJsonAsync(sign, new { typedName = "N", hash }, Ct), HttpStatusCode.BadRequest, "waiver.typed_name");
        JsonElement signature = await JsonAsync(await family.PostAsJsonAsync(sign, new { typedName = "Ngozi Nwosu", hash }, Ct));
        signature.GetProperty("signerRole").GetString().ShouldBe("guardian");
        (await JsonAsync(await family.PostAsJsonAsync(sign, new { typedName = "Ngozi Nwosu", hash }, Ct))).GetProperty("id").GetGuid()
            .ShouldBe(signature.GetProperty("id").GetGuid());
        (await api.ScalarAsync<string>("select encode(body_hash_signed, 'hex') from enrollment.waiver_signature where id = @s", ("s", signature.GetProperty("id").GetGuid()))).ShouldBe(hash);

        // Signed, it goes in; the optional one can stay unsigned.
        (await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct)))
            .GetProperty("status").GetString().ShouldBe("submitted");

        // A signature is evidence: nobody, not even a superuser, rewrites or deletes it.
        await Should.ThrowAsync<PostgresException>(() => api.ExecuteAsync("delete from enrollment.waiver_signature where id = @s", ("s", signature.GetProperty("id").GetGuid())));

        // A new version needs a new signature; the old one stays, and can retire once nothing asks for it.
        await ProblemAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{release}/retire", UriKind.Relative), null, Ct), HttpStatusCode.Conflict, "waiver.in_use");
        Guid v2 = (await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{release}/revise", UriKind.Relative), null, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await provider.PutAsJsonAsync($"api/v1/org/{org}/waivers/{v2}", new { body = "Version two of the release." }, Ct));
        await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{v2}/publish", UriKind.Relative), null, Ct));
        (await provider.DeleteAsync(new Uri($"api/v1/org/{org}/listings/{listing}/waivers/{release}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waivers/{v2}", new { isRequired = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement retired = await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{release}/retire", UriKind.Relative), null, Ct));
        retired.GetProperty("status").GetString().ShouldBe("retired");
        retired.GetProperty("signatureCount").GetInt32().ShouldBe(1);
        JsonElement now = (await family.GetFromJsonAsync<JsonElement>($"api/v1/applications/{application}/waivers", Ct)).EnumerateArray()
            .Single(w => w.GetProperty("waiverId").GetGuid() == v2);
        now.GetProperty("version").GetInt32().ShouldBe(2);
        now.GetProperty("signedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Adults_sign_for_themselves_and_others_are_kept_out()
    {
        RequireDatabase();
        (Guid listing, Guid org) = OwnListing();
        using HttpClient provider = await SignedInAsync(AdminOf(org));
        (string strangerEmail, Guid strangerOrg) = AllStaff().ElementAt(12);
        using HttpClient family = await SignedInAsync(AllStaff().ElementAt(11).Email);
        using HttpClient stranger = await SignedInAsync(strangerEmail);

        await ProblemAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/waivers", new { title = "Empty", body = "   " }, Ct), HttpStatusCode.BadRequest, "waiver.body");
        Guid medical = (await JsonAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/waivers",
            new { title = "Medical consent", body = "Staff may call emergency services." }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await ProblemAsync(await provider.PostAsJsonAsync($"api/v1/org/{org}/waivers", new { title = "Medical consent", body = "Again." }, Ct), HttpStatusCode.Conflict, "waiver.title_taken");
        await JsonAsync(await provider.PostAsync(new Uri($"api/v1/org/{org}/waivers/{medical}/publish", UriKind.Relative), null, Ct));
        (await provider.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/waivers/{medical}", new { isRequired = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A 20-year-old in the household signs their own waivers; a parent can't sign for them.
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct));
        Guid grownUp = await ApplyAsync(family, listing, "Chuka", YearsAgo(20));
        string hash = (await family.GetFromJsonAsync<JsonElement>($"api/v1/applications/{grownUp}/waivers", Ct)).EnumerateArray()
            .Single(w => w.GetProperty("waiverId").GetGuid() == medical).GetProperty("hash").GetString()!;
        await ProblemAsync(await family.PostAsJsonAsync($"api/v1/applications/{grownUp}/waivers/{medical}/sign", new { typedName = "Ngozi Nwosu", hash }, Ct),
            HttpStatusCode.Forbidden, "waiver.adult_signs");

        // Another family sees nothing of this application, and staff don't write waivers.
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/applications/{grownUp}/waivers", UriKind.Relative), Ct), HttpStatusCode.NotFound, "application.not_found");
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/applications/{grownUp}/waivers/{medical}/sign", new { typedName = "Someone Else", hash }, Ct),
            HttpStatusCode.NotFound, "application.not_found");
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/org/{strangerOrg}/waivers", UriKind.Relative), Ct), HttpStatusCode.Forbidden, "form.forbidden");
    }
}
