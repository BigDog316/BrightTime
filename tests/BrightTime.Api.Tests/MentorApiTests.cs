using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Mentors from the API (D129): a mentor reads and writes their own profile and free times; someone
/// who isn't a mentor can't; an organisation's staff search, and find a listed mentor once cleared;
/// a mentor doesn't search for an organisation.
/// </summary>
[Collection(ApiDatabase.Name)]
public sealed class MentorApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static Uri Relative(string path) => new(path, UriKind.Relative);

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

    [Fact]
    public async Task A_mentor_keeps_a_profile_and_staff_find_them_once_cleared()
    {
        RequireDatabase();
        List<Guid> orgs = [.. api.Data.Organisations.Where(o => o.Status == "approved" && api.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org"))
            .OrderByDescending(o => o.Slug, StringComparer.Ordinal).Take(2).Select(o => o.Id)];
        string staffEmail = api.Data.Users.First(u => u.Email == $"staff@{api.Data.Organisations.Single(o => o.Id == orgs[1]).Slug}.example.org").Email;
        string email = $"mentor-{ApiFixture.Letters()}@example.org";
        Guid mentor = Guid.NewGuid();
        await api.ExecuteAsync(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                                           security_stamp, concurrency_stamp, account_kind)
            values (@u, @t, @e, upper(@e), @e, upper(@e), true, 'stamp', 'concurrency', 'adult');
            insert into identity.app_user_role (user_id, role_id) select @u, r.id from identity.app_role r where r.tenant_id = @t and r.name = 'mentor';
            insert into directory.org_membership (id, tenant_id, org_id, user_id, org_role, status, joined_at)
            values (gen_random_uuid(), @t, @o, @u, 'mentor', 'active', now());
            """,
            ("u", mentor), ("t", api.Data.TenantId), ("e", email), ("o", orgs[0]));
        using HttpClient mine = await SignedInAsync(email);
        using HttpClient staff = await SignedInAsync(staffEmail);

        JsonElement empty = await JsonAsync(await mine.GetAsync(Relative("api/v1/me/mentor-profile"), Ct));
        (empty.GetProperty("isMentor").GetBoolean(), empty.GetProperty("listed").GetBoolean()).ShouldBe((true, false));
        string name = "Sam " + ApiFixture.Letters();
        await ProblemAsync(await mine.PutAsJsonAsync("api/v1/me/mentor-profile", new { listed = true, willingVirtual = true }, Ct), HttpStatusCode.BadRequest, "mentors.name");
        JsonElement saved = await JsonAsync(await mine.PutAsJsonAsync("api/v1/me/mentor-profile",
            new { displayName = name, headline = "Chess and maths", willingVirtual = true, weeklyHoursSought = 3, listed = true }, Ct));
        (saved.GetProperty("listed").GetBoolean(), saved.GetProperty("findable").GetBoolean()).ShouldBe((true, false), "not cleared yet");
        JsonElement free = await JsonAsync(await mine.PutAsJsonAsync("api/v1/me/mentor-profile/availability",
            new { slots = new[] { new { dayOfWeek = 4, from = "17:00:00", to = "19:00:00" } } }, Ct));
        free.GetProperty("availability").EnumerateArray().Single().GetProperty("dayOfWeek").GetInt32().ShouldBe(4);
        await ProblemAsync(await mine.PutAsJsonAsync("api/v1/me/mentor-profile/availability",
            new { slots = new[] { new { dayOfWeek = 9, from = "17:00:00", to = "19:00:00" } } }, Ct), HttpStatusCode.BadRequest, "mentors.availability");

        // Someone who isn't a mentor has no profile here; a mentor doesn't search for an organisation.
        await ProblemAsync(await staff.GetAsync(Relative("api/v1/me/mentor-profile"), Ct), HttpStatusCode.Forbidden, "mentors.not_mentor");
        await ProblemAsync(await mine.GetAsync(Relative($"api/v1/org/{orgs[0]}/mentors/search"), Ct), HttpStatusCode.Forbidden, "mentors.forbidden");

        string search = $"api/v1/org/{orgs[1]}/mentors/search?dayOfWeek=4";
        (await JsonAsync(await staff.GetAsync(Relative(search), Ct))).EnumerateArray().Any(m => m.GetProperty("userId").GetGuid() == mentor).ShouldBeFalse();
        await api.ExecuteAsync(
            """
            insert into directory.staff_clearance (id, tenant_id, user_id, org_id, clearance_kind, status, completed_on, expires_on)
            values (gen_random_uuid(), @t, @u, null, 'background_check', 'cleared', current_date - 3, current_date + 300),
                   (gen_random_uuid(), @t, @u, null, 'mandated_reporter_training', 'cleared', current_date - 3, null);
            """, ("t", api.Data.TenantId), ("u", mentor));
        JsonElement card = (await JsonAsync(await staff.GetAsync(Relative(search), Ct))).EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == mentor);
        (card.GetProperty("displayName").GetString(), card.GetProperty("email").GetString()).ShouldBe((name, email));
        (await JsonAsync(await staff.GetAsync(Relative($"api/v1/org/{orgs[1]}/mentors/search?dayOfWeek=1"), Ct))).EnumerateArray()
            .Any(m => m.GetProperty("userId").GetGuid() == mentor).ShouldBeFalse();
    }
}
