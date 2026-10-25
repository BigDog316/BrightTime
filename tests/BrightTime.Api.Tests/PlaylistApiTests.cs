using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Playlists (M4, D67), and the rest of M4's acceptance criterion: completing a playlist issues
/// the badge and unlocks a gated listing. An organisation writes and publishes a playlist; a young
/// person works through it, a step reviewed by staff and sent back once; completing it awards the
/// badge, which opens a listing gated on it and a second playlist that asks for it.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the eleventh organisation with no staff (BadgeApiTests takes
/// the tenth); the ninth's admin is the outsider. The young people are fresh adult accounts.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class PlaylistApiTests(ApiFixture api)
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
        return unstaffed.First(l => l.OrgId == org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<(HttpClient Client, Guid User)> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        Guid user = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email));
        return (ApiFixture.WithBearer(api.Client(), tokens.AccessToken), user);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static string[] States(JsonElement playlist) => [.. playlist.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("state").GetString()!)];

    /// <summary>A young person's own household entry, linked to their own account, with a drafted application for a listing.</summary>
    private static async Task<Guid> DraftApplicationAsync(HttpClient learner, Guid learnerId, Guid listing)
    {
        await JsonAsync(await learner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-19).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await learner.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Ayo", lastName = "Bankole", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = learnerId }, Ct));
        using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        apply.Headers.Add("Idempotency-Key", "playlist-" + Guid.NewGuid().ToString("N"));
        return (await JsonAsync(await learner.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Completing_a_playlist_awards_its_badge_which_opens_a_gated_listing_and_a_gated_playlist()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme(10);
        (_, Guid otherOrg) = Programme(8);
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient outsider, _) = await SignedInAsync(AdminOf(otherOrg));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        (HttpClient newcomer, _) = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anonymous = api.Client();
        using (admin)
        using (outsider)
        using (learner)
        using (newcomer)
        {
            // The badge at the end, published by the organisation.
            Guid badge = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes",
                new { name = "Watershed Explorer", description = "Explored the county's watershed.", criteria = "Completed the Watershed Explorer playlist and its check-in." }, Ct),
                HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{badge}/publish"), null, Ct));

            // The listing gated on it: families see the rule; a made-up applicant with the badge passes, one without doesn't.
            await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility",
                new { rules = new[] { new { kind = "badge_required", config = new { badgeClassId = Guid.NewGuid() }, isHard = true } } }, Ct), HttpStatusCode.BadRequest, "eligibility.achievement");
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility",
                new { rules = new[] { new { kind = "badge_required", config = new { badgeClassId = badge }, isHard = true } } }, Ct));
            (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility/preview", new { badgeClassIds = new[] { badge } }, Ct)))
                .GetProperty("isEligible").GetBoolean().ShouldBeTrue();
            (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/listings/{listing}/eligibility/preview", new { }, Ct)))
                .GetProperty("isEligible").GetBoolean().ShouldBeFalse();

            // A draft playlist: kinds without their data are refused, and so is publishing with no steps.
            object details = new { title = "Watershed Explorer", description = "Three steps along the river.", isOrdered = true, ageMin = 13, ageMax = 19, estimatedHours = 6.5m, completionBadgeClassId = badge };
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{org}/playlists", details, Ct), HttpStatusCode.Forbidden, "playlists.forbidden");
            JsonElement made = await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists", details, Ct), HttpStatusCode.Created);
            Guid playlist = made.GetProperty("id").GetGuid();
            string slug = made.GetProperty("slug").GetString()!;
            (made.GetProperty("status").GetString(), made.GetProperty("completionBadge").GetString()).ShouldBe(("draft", "Watershed Explorer"));
            await ProblemAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/publish"), null, Ct), HttpStatusCode.BadRequest, "playlists.unpublishable");
            await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps",
                new { steps = new[] { new { kind = "upload_artifact", title = "A photo" } } }, Ct), HttpStatusCode.BadRequest, "playlists.step_kind");
            await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps",
                new { steps = new[] { new { kind = "external_activity", title = "Read up", externalUrl = "http://example.org/" } } }, Ct), HttpStatusCode.BadRequest, "playlists.step_link");

            // Three steps in order: a reflection, a check-in staff review, and an optional session at the listing.
            JsonElement stepped = await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps", new
            {
                steps = new object[]
                {
                    new { kind = "reflection", title = "What lives in a river?", instructions = "Write what you expect to find." },
                    new { kind = "mentor_checkin", title = "Check in with a ranger" },
                    new { kind = "attend_listing", title = "Come to a session", listingId = listing, isRequired = false },
                },
            }, Ct));
            JsonElement[] steps = [.. stepped.GetProperty("steps").EnumerateArray()];
            (steps[1].GetProperty("requiresReview").GetBoolean(), steps[2].GetProperty("isRequired").GetBoolean()).ShouldBe((true, false));
            Guid reflect = steps[0].GetProperty("id").GetGuid(), checkin = steps[1].GetProperty("id").GetGuid(), attend = steps[2].GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/publish"), null, Ct));
            await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps",
                new { steps = new[] { new { kind = "reflection", title = "Changed" } } }, Ct), HttpStatusCode.Conflict, "playlists.not_draft");

            // Anyone reads it.
            JsonElement shown = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/playlists/{slug}"), Ct));
            (shown.GetProperty("organisation").GetString(), shown.GetProperty("completionBadge").GetString(), shown.GetProperty("steps").GetArrayLength())
                .ShouldBe((api.Data.Organisations.Single(o => o.Id == org).DisplayName, "Watershed Explorer", 3));
            (await JsonAsync(await anonymous.GetAsync(Relative("api/v1/public/playlists"), Ct))).EnumerateArray().Select(p => p.GetProperty("slug").GetString()).ShouldContain(slug);

            // Before the badge, the gated listing refuses the young person's application.
            Guid application = await DraftApplicationAsync(learner, learnerId, listing);
            await ProblemAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct),
                HttpStatusCode.Conflict, "application.ineligible");

            // The young person starts it; the steps open in order, and a reflection must say something.
            JsonElement mine = await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{playlist}/enroll"), null, Ct));
            States(mine).ShouldBe(["not_started", "not_started", "not_started"]);
            mine.GetProperty("steps")[1].GetProperty("available").GetBoolean().ShouldBeFalse();
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{checkin}", new { }, Ct), HttpStatusCode.Conflict, "playlists.out_of_order");
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{reflect}", new { reflection = "Fish." }, Ct), HttpStatusCode.BadRequest, "playlists.reflection");
            States(await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{reflect}",
                new { reflection = "Mayfly nymphs under the stones, and maybe a crayfish." }, Ct))).ShouldBe(["completed", "not_started", "not_started"]);
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{reflect}", new { reflection = "Again and again and again." }, Ct),
                HttpStatusCode.Conflict, "playlists.step_done");
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{attend}", new { }, Ct), HttpStatusCode.Conflict, "playlists.out_of_order");
            States(await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{checkin}", new { }, Ct))).ShouldBe(["completed", "submitted", "not_started"]);
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{checkin}", new { }, Ct), HttpStatusCode.Conflict, "playlists.awaiting_review");

            // Staff see what waits, named by nothing but a portfolio display name, and send it back once.
            JsonElement waiting = (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/submissions"), Ct))).EnumerateArray().Single();
            Guid enrollment = waiting.GetProperty("enrollmentId").GetGuid();
            (waiting.GetProperty("stepId").GetGuid(), waiting.GetRawText().Contains(learnerId.ToString(), StringComparison.Ordinal)).ShouldBe((checkin, false));
            await ProblemAsync(await outsider.GetAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/submissions"), Ct), HttpStatusCode.Forbidden, "playlists.forbidden");
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps/{checkin}/review", new { enrollmentId = enrollment, approve = false }, Ct),
                HttpStatusCode.BadRequest, "playlists.review_note");
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps/{checkin}/review",
                new { enrollmentId = enrollment, approve = false, note = "Tell the ranger what you found under the stones." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            JsonElement back = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).EnumerateArray().Single();
            (States(back)[1], back.GetProperty("steps")[1].GetProperty("reviewNote").GetString()).ShouldBe(("needs_rework", "Tell the ranger what you found under the stones."));
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{checkin}", new { reflection = "Told the ranger about the crayfish." }, Ct));

            // Approved, the last required step completes the playlist and awards the badge; the optional step doesn't hold it up.
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps/{checkin}/review", new { enrollmentId = enrollment, approve = true }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.NoContent);
            JsonElement done = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).EnumerateArray().Single();
            done.GetProperty("completedAt").ValueKind.ShouldBe(JsonValueKind.String);
            Guid assertion = done.GetProperty("awardedAssertionId").GetGuid();
            JsonElement held = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/badges"), Ct))).EnumerateArray().Single();
            (held.GetProperty("id").GetGuid(), held.GetProperty("name").GetString()).ShouldBe((assertion, "Watershed Explorer"));
            (await api.ScalarAsync<Guid>("select source_playlist_enrollment_id from learning.badge_assertion where id = @a", ("a", assertion))).ShouldBe(enrollment);
            (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/playlists/{playlist}"), Ct))).GetProperty("completed").GetInt32().ShouldBe(1);
            await ProblemAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{attend}", new { }, Ct), HttpStatusCode.Conflict, "playlists.not_attended");

            // With the badge, the gated listing takes the application.
            (await JsonAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct)))
                .GetProperty("status").GetString().ShouldBe("submitted");

            // A second playlist asks for the badge: closed to someone without it, open to the young person who holds it.
            Guid gated = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists",
                new { title = "Watershed Guide", requiresBadgeClassId = badge }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{gated}/steps", new { steps = new[] { new { kind = "reflection", title = "Lead a walk" } } }, Ct));
            (await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{gated}/publish"), null, Ct))).GetProperty("requiresBadge").GetString().ShouldBe("Watershed Explorer");
            await ProblemAsync(await newcomer.PostAsync(Relative($"api/v1/playlists/{gated}/enroll"), null, Ct), HttpStatusCode.Forbidden, "playlists.locked");
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{gated}/enroll"), null, Ct));

            // Archived, it takes nobody new; the young person already on it carries on.
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{gated}/archive"), null, Ct));
            await ProblemAsync(await newcomer.PostAsync(Relative($"api/v1/playlists/{gated}/enroll"), null, Ct), HttpStatusCode.Conflict, "playlists.not_published");
            (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).GetArrayLength().ShouldBe(2);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.playlist.reviewed' and @e = any(subject_ids)", ("e", enrollment))).ShouldBe(2);
        }
    }
}
