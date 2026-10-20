using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// The family profile (M3a, D43): a household entered once, its youth details encrypted, every
/// read and change audited, and nobody but its owner able to see or touch it.
/// </summary>
/// <remarks>Staff accounts 0 to 2 (every admin is taken); no other API test signs in as staff.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class FamilyProfileApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Staff(int index) =>
        api.Data.Users.Select(u => u.Email).Where(e => e.StartsWith("staff@", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ElementAt(index);

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    private static object Youth(string first, string? dob = "2014-05-17") =>
        new { role = "youth", firstName = first, lastName = "Rivera", dateOfBirth = dob, grade = 6, pronouns = "she/her" };

    [Fact]
    public async Task A_household_is_kept_encrypted_and_read_back_whole()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Staff(0));

        HttpResponseMessage none = await me.GetAsync(new Uri("api/v1/family-profile", UriKind.Relative), Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(none)).ShouldBe("urn:brighttime:problem:family.no_profile");
        (await me.PostAsJsonAsync("api/v1/family-profile/members", Youth("Ana"), Ct)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        HttpResponseMessage saved = await me.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 4, primaryLanguage = "es", needsInterpreter = true }, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid profile = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        HttpResponseMessage undated = await me.PostAsJsonAsync("api/v1/family-profile/members", Youth("Ana", dob: null), Ct);
        undated.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(undated)).ShouldBe("urn:brighttime:problem:family.member_birth_date");

        HttpResponseMessage added = await me.PostAsJsonAsync("api/v1/family-profile/members", Youth("Ana"), Ct);
        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid member = (await added.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        HttpResponseMessage needs = await me.PutAsJsonAsync($"api/v1/family-profile/members/{member}/support-needs",
            new { needs = new[] { new { kind = "allergy", detail = "Peanuts; carries an EpiPen", shareWithProviders = true } } }, Ct);
        needs.StatusCode.ShouldBe(HttpStatusCode.OK);

        HttpResponseMessage contact = await me.PostAsJsonAsync("api/v1/family-profile/contacts",
            new { name = "Luis Rivera", relationship = "uncle", phone = "(718) 555-0142", email = "luis@example.org", canPickUp = true }, Ct);
        contact.StatusCode.ShouldBe(HttpStatusCode.Created);

        JsonElement family = await me.GetFromJsonAsync<JsonElement>("api/v1/family-profile", Ct);
        family.GetProperty("householdSize").GetInt32().ShouldBe(4);
        family.GetProperty("primaryLanguage").GetString().ShouldBe("es");
        JsonElement ana = family.GetProperty("members").EnumerateArray().Single();
        ana.GetProperty("firstName").GetString().ShouldBe("Ana");
        ana.GetProperty("dateOfBirth").GetString().ShouldBe("2014-05-17");
        ana.GetProperty("supportNeeds")[0].GetProperty("detail").GetString().ShouldBe("Peanuts; carries an EpiPen");
        JsonElement luis = family.GetProperty("contacts").EnumerateArray().Single();
        luis.GetProperty("phone").GetString().ShouldBe("(718) 555-0142");
        luis.GetProperty("isEmergency").GetBoolean().ShouldBeTrue();
        luis.GetProperty("canPickUp").GetBoolean().ShouldBeTrue();

        // Only ciphertext in the tables; the birth month alone is kept in the clear.
        string rivera = Convert.ToHexStringLower(Encoding.UTF8.GetBytes("Rivera"));
        (await api.ScalarAsync<string>("select encode(last_name_enc, 'hex') from enrollment.family_member where id = @m", ("m", member))).ShouldNotContain(rivera);
        (await api.ScalarAsync<string>("select encode(detail_enc, 'hex') from enrollment.family_member_support_need where family_member_id = @m", ("m", member)))
            .ShouldNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("Peanuts")));
        (await api.ScalarAsync<string>("select encode(name_enc, 'hex') from enrollment.family_contact where family_profile_id = @p", ("p", profile))).ShouldNotContain(rivera);
        (await api.ScalarAsync<DateOnly>("select birth_month from enrollment.family_member where id = @m", ("m", member))).ShouldBe(new DateOnly(2014, 5, 1));
        (await api.ScalarAsync<bool>("select phone_blind_index is not null from enrollment.family_contact where family_profile_id = @p", ("p", profile))).ShouldBeTrue();

        // The read and the refusal both left a row.
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.family.read' and subject_ids = array[@p]", ("p", profile)))
            .ShouldBeGreaterThanOrEqualTo(1);
        (await api.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'enrollment.family.changed' and outcome = 'deny' and reason = 'family.member_birth_date' and actor_id = @u",
            ("u", Seed.SeedIds.For("user/" + Staff(0))))).ShouldBe(1);
    }

    [Fact]
    public async Task Nobody_but_the_owner_can_see_or_change_a_household()
    {
        RequireDatabase();
        using HttpClient owner = await SignedInAsync(Staff(1));
        using HttpClient other = await SignedInAsync(Staff(2));

        (await owner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid member = (await (await owner.PostAsJsonAsync("api/v1/family-profile/members", Youth("Maya"), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        Guid contact = (await (await owner.PostAsJsonAsync("api/v1/family-profile/contacts",
            new { name = "June Park", relationship = "neighbour", phone = "718-555-0199" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/support-needs",
            new { needs = new[] { new { kind = "sensory", detail = "Loud rooms are hard", shareWithProviders = false } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Another account, with a household of its own, reaches nothing of this one.
        (await other.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage theirs = await other.PutAsJsonAsync($"api/v1/family-profile/members/{member}", Youth("Taken"), Ct);
        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemAsync(theirs)).ShouldBe("urn:brighttime:problem:family.member_not_found");
        (await other.PutAsJsonAsync($"api/v1/family-profile/members/{member}/support-needs", new { needs = Array.Empty<object>() }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync(new Uri($"api/v1/family-profile/members/{member}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.DeleteAsync(new Uri($"api/v1/family-profile/contacts/{contact}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<JsonElement>("api/v1/family-profile", Ct)).GetProperty("members").GetArrayLength().ShouldBe(0);

        HttpResponseMessage unknown = await owner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/support-needs",
            new { needs = new[] { new { kind = "astrology", detail = (string?)null, shareWithProviders = false } } }, Ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(unknown)).ShouldBe("urn:brighttime:problem:family.need_kind");

        // The owner removes the member, and the support needs go with them.
        (await owner.DeleteAsync(new Uri($"api/v1/family-profile/members/{member}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.ScalarAsync<long>("select count(*) from enrollment.family_member_support_need where family_member_id = @m", ("m", member))).ShouldBe(0);
        (await owner.DeleteAsync(new Uri($"api/v1/family-profile/contacts/{contact}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        JsonElement left = await owner.GetFromJsonAsync<JsonElement>("api/v1/family-profile", Ct);
        left.GetProperty("members").GetArrayLength().ShouldBe(0);
        left.GetProperty("contacts").GetArrayLength().ShouldBe(0);
    }
}
