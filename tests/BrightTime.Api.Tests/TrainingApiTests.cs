using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Training from the API (D130): an organisation's staff member reads the catalogue and a course,
/// marks its units done, completing it, and sees the course the county requires of staff counted;
/// a unit of another course and a course that isn't published are refused.
/// </summary>
[Collection(ApiDatabase.Name)]
public sealed class TrainingApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    [Fact]
    public async Task Staff_take_a_required_course_unit_by_unit()
    {
        RequireDatabase();
        string staffEmail = api.Data.Users.Select(u => u.Email).Where(e => e.StartsWith("staff@", StringComparison.Ordinal)).Order(StringComparer.Ordinal).First();
        string slug = "first-aid-" + ApiFixture.Letters();
        Guid course = Guid.NewGuid();
        Guid draft = Guid.NewGuid();
        Guid[] units = [Guid.NewGuid(), Guid.NewGuid()];
        await api.ExecuteAsync(
            """
            insert into workforce.course (id, tenant_id, slug, title, status, published_at, validity_months)
            values (@c, @t, @s, 'First aid', 'published', now(), 24),
                   (@d, @t, @s || '-draft', 'First aid, next year', 'draft', null, null);
            insert into workforce.course_unit (id, course_id, sort_order, title, body_md) values
              (@u1, @c, 1, 'The recovery position', 'Roll them onto their side.'),
              (@u2, @c, 2, 'Calling for help', 'Call 911.'),
              (gen_random_uuid(), @d, 1, 'Draft unit', 'Not yet.');
            insert into workforce.training_requirement (tenant_id, applies_to, course_id, is_blocking, grace_days) values (@t, 'staff', @c, true, 0);
            """,
            ("c", course), ("d", draft), ("t", api.Data.TenantId), ("s", slug), ("u1", units[0]), ("u2", units[1]));
        try
        {
            string password = await api.SetPasswordAsync(staffEmail);
            TokenPair tokens = await api.SignInAsync(staffEmail, password);
            using HttpClient staff = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);

            JsonElement catalogue = await JsonAsync(await staff.GetAsync(Relative("api/v1/courses"), Ct));
            JsonElement entry = catalogue.EnumerateArray().Single(c => c.GetProperty("slug").GetString() == slug);
            (entry.GetProperty("state").GetString(), entry.GetProperty("required").GetBoolean(), entry.GetProperty("units").GetInt32()).ShouldBe(("not_started", true, 2));
            catalogue.EnumerateArray().ShouldNotContain(c => c.GetProperty("slug").GetString() == slug + "-draft");
            await ProblemAsync(await staff.GetAsync(Relative($"api/v1/courses/{slug}-draft"), Ct), HttpStatusCode.NotFound, "training.no_course");

            JsonElement status = await JsonAsync(await staff.GetAsync(Relative("api/v1/me/training"), Ct));
            status.GetProperty("current").GetBoolean().ShouldBeFalse();

            await ProblemAsync(await staff.PutAsync(Relative($"api/v1/courses/{course}/units/{Guid.NewGuid()}/complete"), null, Ct), HttpStatusCode.NotFound, "training.no_unit");
            JsonElement first = await JsonAsync(await staff.PutAsync(Relative($"api/v1/courses/{course}/units/{units[0]}/complete"), null, Ct));
            first.GetProperty("state").GetString().ShouldBe("in_progress");
            JsonElement done = await JsonAsync(await staff.PutAsync(Relative($"api/v1/courses/{course}/units/{units[1]}/complete"), null, Ct));
            done.GetProperty("state").GetString().ShouldBe("completed");
            done.GetProperty("expiresOn").GetString().ShouldNotBeNull();

            JsonElement read = await JsonAsync(await staff.GetAsync(Relative($"api/v1/courses/{slug}"), Ct));
            read.GetProperty("done").GetArrayLength().ShouldBe(2);
            status = await JsonAsync(await staff.GetAsync(Relative("api/v1/me/training"), Ct));
            status.GetProperty("current").GetBoolean().ShouldBeTrue();
            status.GetProperty("required").EnumerateArray().Single(r => r.GetProperty("slug").GetString() == slug).GetProperty("state").GetString().ShouldBe("completed");
        }
        finally
        {
            // The requirement would count against every other test's staff.
            await api.ExecuteAsync("delete from workforce.training_requirement where course_id = @c", ("c", course));
        }
    }
}
