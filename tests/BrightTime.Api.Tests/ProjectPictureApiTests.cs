using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.SharedKernel.Tests;

namespace BrightTime.Api.Tests;

/// <summary>
/// A young person's pictures (D104): on a project, checked and cleaned, kept private, and shown
/// beyond them only to staff while it waits for their review and to anyone once it's published.
/// An upload step sends a project with a picture to staff as its evidence.
/// </summary>
/// <remarks>Uses the twentieth organisation with no staff (the API tests use the others up to it); the ninth's admin is the outsider.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class ProjectPictureApiTests(ApiFixture api)
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

    /// <summary>A picture as the app sends it: a form with the file, what it shows, and a caption.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Each part is owned, and disposed, by the multipart content it is added to.")]
    private static async Task<HttpResponseMessage> PostPictureAsync(HttpClient client, string path, byte[] file, string type, string? alt, string? caption = null)
    {
        using MultipartFormDataContent form = new();
        ByteArrayContent bytes = new(file);
        bytes.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(bytes, "file", type == "image/png" ? "work.png" : "work.jpg");
        if (alt is not null)
        {
            form.Add(new StringContent(alt), "alt");
        }

        if (caption is not null)
        {
            form.Add(new StringContent(caption), "caption");
        }

        return await client.PostAsync(Relative(path), form, Ct);
    }

    private Task AttendedAsync(Guid learner, Guid listing, Guid org) =>
        api.ExecuteAsync("""
            insert into learning.participation_record (id, tenant_id, user_id, listing_id, org_id, title_snapshot, org_name_snapshot, started_on, sessions_attended, hours, source, created_at)
            values (gen_random_uuid(), @t, @u, @l, @o, 'The activity', 'The organisation', current_date - 30, 6, 9, 'attendance', now())
            """, ("t", api.Data.TenantId), ("u", learner), ("l", listing), ("o", org));

    private static async Task<Guid> ProjectAsync(HttpClient learner, Guid listing, string title) =>
        (await JsonAsync(await learner.PostAsJsonAsync("api/v1/me/projects", new { title, description = "Made by hand.", listingId = listing }, Ct), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    [Fact]
    public async Task A_picture_is_cleaned_kept_private_seen_by_reviewing_staff_and_shown_only_once_published()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme(19);
        (_, Guid otherOrg) = Programme(8);
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient outsider, _) = await SignedInAsync(AdminOf(otherOrg));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        (HttpClient someoneElse, _) = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anonymous = api.Client();
        using (admin)
        using (outsider)
        using (learner)
        using (someoneElse)
        {
            await AttendedAsync(learnerId, listing, org);
            Guid projectId = await ProjectAsync(learner, listing, "A clay boat");
            string pictures = $"api/v1/me/projects/{projectId}/pictures";

            // Refused: no description, a file that isn't a picture, one too small, and someone else's project.
            await ProblemAsync(await PostPictureAsync(learner, pictures, TestImages.Png(800, 600), "image/png", null), HttpStatusCode.BadRequest, "projects.picture_alt");
            await ProblemAsync(await PostPictureAsync(learner, pictures, "<svg/>"u8.ToArray(), "image/png", "A boat"), HttpStatusCode.BadRequest, "projects.picture");
            await ProblemAsync(await PostPictureAsync(learner, pictures, TestImages.Png(300, 600), "image/png", "A boat"), HttpStatusCode.BadRequest, "projects.picture");
            await ProblemAsync(await PostPictureAsync(someoneElse, pictures, TestImages.Png(800, 600), "image/png", "A boat"), HttpStatusCode.NotFound, "projects.not_found");
            (await api.ScalarAsync<long>("select count(*) from platform.media_object where uploaded_by = @u", ("u", learnerId))).ShouldBe(0, "a refused file leaves nothing behind");

            // Added: cleaned (the text that named a street is gone), private, and theirs alone to fetch.
            JsonElement added = await JsonAsync(await PostPictureAsync(learner, pictures, TestImages.Png(800, 600, "Made at 12 Elm Street"), "image/png", "A clay boat on a table", "Before the glaze"));
            JsonElement picture = added.GetProperty("pictures").EnumerateArray().Single();
            Guid media = picture.GetProperty("mediaId").GetGuid();
            (picture.GetProperty("alt").GetString(), picture.GetProperty("caption").GetString()).ShouldBe(("A clay boat on a table", "Before the glaze"));
            (await api.ScalarAsync<string>("select visibility from platform.media_object where id = @m", ("m", media))).ShouldBe("private");
            HttpResponseMessage own = await learner.GetAsync(Relative($"{pictures}/{media}"), Ct);
            own.StatusCode.ShouldBe(HttpStatusCode.OK);
            (own.Content.Headers.ContentType!.MediaType, own.Headers.CacheControl!.NoStore).ShouldBe(("image/png", true));
            (await own.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(TestImages.Png(800, 600), "the same picture, without its text");
            (await someoneElse.GetAsync(Relative($"api/v1/me/projects/{projectId}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await anonymous.GetAsync(Relative($"api/v1/me/projects/{projectId}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            // Staff see it while it waits for them, and not before or after; another organisation never.
            string review = $"api/v1/org/{org}/pictures/{media}";
            (await admin.GetAsync(Relative(review), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a draft isn't theirs to see yet");
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/me/projects/{projectId}/submit"), null, Ct));
            JsonElement waiting = (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/projects"), Ct))).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == projectId);
            waiting.GetProperty("pictures")[0].GetProperty("mediaId").GetGuid().ShouldBe(media);
            (await admin.GetAsync(Relative(review), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await outsider.GetAsync(Relative($"api/v1/org/{otherOrg}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await outsider.GetAsync(Relative(review), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/projects/{projectId}/review", new { approve = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await admin.GetAsync(Relative(review), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "approved, it's no longer waiting for them");

            // Published on a shown portfolio, anyone sees it there, by its project and portfolio.
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}/visibility", new { visibility = "public" }, Ct));
            string slug = "clay-" + Guid.NewGuid().ToString("N")[..8];
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Sam Clay", visibility = "link_only", publicSlug = slug }, Ct));
            JsonElement page = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/portfolios/{slug}"), Ct));
            JsonElement shown = page.GetProperty("projects").EnumerateArray().Single();
            (shown.GetProperty("id").GetGuid(), shown.GetProperty("pictures")[0].GetProperty("alt").GetString()).ShouldBe((projectId, "A clay boat on a table"));
            string open = $"api/v1/public/portfolios/{slug}/projects/{projectId}/pictures/{media}";
            HttpResponseMessage seen = await anonymous.GetAsync(Relative(open), Ct);
            seen.StatusCode.ShouldBe(HttpStatusCode.OK);
            seen.Headers.CacheControl!.NoStore.ShouldBeTrue();
            (await anonymous.GetAsync(Relative($"api/v1/public/portfolios/someone-else/projects/{projectId}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // Taking a picture away keeps the approval: the rest is still what staff saw.
            JsonElement second = await JsonAsync(await PostPictureAsync(learner, pictures, TestImages.Jpeg(1600, 1200, orientation: 6), "image/jpeg", "The boat from above"));
            second.GetProperty("status").GetString().ShouldBe("draft", "a new picture waits for approval like any change");
            (await anonymous.GetAsync(Relative(open), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a draft isn't shown");
            Guid later = second.GetProperty("pictures")[1].GetProperty("mediaId").GetGuid();
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/me/projects/{projectId}/submit"), null, Ct));
            (await admin.PostAsJsonAsync($"api/v1/org/{org}/projects/{projectId}/review", new { approve = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/projects/{projectId}/visibility", new { visibility = "public" }, Ct));
            JsonElement fewer = await JsonAsync(await learner.DeleteAsync(Relative($"{pictures}/{later}"), Ct));
            (fewer.GetProperty("status").GetString(), fewer.GetProperty("pictures").GetArrayLength()).ShouldBe(("published", 1));
            (await api.ScalarAsync<long>("select count(*) from platform.media_blob where object_key like '%' || replace(@m::text, '-', '') || '%'", ("m", later))).ShouldBe(0, "its bytes go with it");
            (await anonymous.GetAsync(Relative(open), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // Describing one again is a change like any other.
            JsonElement described = await JsonAsync(await learner.PutAsJsonAsync($"{pictures}/{media}", new { alt = "A small clay boat on a table", caption = (string?)null }, Ct));
            (described.GetProperty("status").GetString(), described.GetProperty("pictures")[0].GetProperty("caption").ValueKind).ShouldBe(("draft", JsonValueKind.Null));

            // Six at most.
            for (int i = 0; i < 5; i++)
            {
                await JsonAsync(await PostPictureAsync(learner, pictures, TestImages.Png(400, 400), "image/png", "Detail " + i));
            }

            await ProblemAsync(await PostPictureAsync(learner, pictures, TestImages.Png(400, 400), "image/png", "One more"), HttpStatusCode.Conflict, "projects.pictures_full");

            // Removing the project takes every picture's bytes with it.
            (await learner.DeleteAsync(Relative($"api/v1/me/projects/{projectId}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await api.ScalarAsync<long>("select count(*) from learning.project_media where project_id = @p", ("p", projectId))).ShouldBe(0);
            (await api.ScalarAsync<long>("select count(*) from platform.media_object where uploaded_by = @u", ("u", learnerId))).ShouldBe(0);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'media.added' and actor_id = @u", ("u", learnerId))).ShouldBe(7);
        }
    }

    [Fact]
    public async Task An_upload_step_sends_a_project_with_a_picture_to_staff_who_see_it_while_it_waits()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme(19);
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        using (admin)
        using (learner)
        {
            Guid playlist = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists",
                new { title = "Show Your Making " + Guid.NewGuid().ToString("N")[..6], isOrdered = false }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            JsonElement written = await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps",
                new { steps = new[] { new { kind = "upload_artifact", title = "Show what you made", requiresReview = false } } }, Ct));
            JsonElement upload = written.GetProperty("steps")[0];
            Guid step = upload.GetProperty("id").GetGuid();
            upload.GetProperty("requiresReview").GetBoolean().ShouldBeTrue("staff always check pictures of work");
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/publish"), null, Ct));
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{playlist}/enroll"), null, Ct));

            await AttendedAsync(learnerId, listing, org);
            Guid bare = await ProjectAsync(learner, listing, "Words only");
            Guid made = await ProjectAsync(learner, listing, "A woven basket");
            Guid media = (await JsonAsync(await PostPictureAsync(learner, $"api/v1/me/projects/{made}/pictures", TestImages.Png(640, 480), "image/png", "A woven basket"))).GetProperty("pictures")[0].GetProperty("mediaId").GetGuid();

            string done = $"api/v1/me/playlists/{playlist}/steps/{step}";
            await ProblemAsync(await learner.PutAsJsonAsync(done, new { reflection = "Here it is." }, Ct), HttpStatusCode.BadRequest, "playlists.evidence");
            await ProblemAsync(await learner.PutAsJsonAsync(done, new { projectId = bare }, Ct), HttpStatusCode.BadRequest, "playlists.evidence");
            await ProblemAsync(await learner.PutAsJsonAsync(done, new { projectId = Guid.NewGuid() }, Ct), HttpStatusCode.BadRequest, "playlists.evidence");
            JsonElement sent = (await JsonAsync(await learner.PutAsJsonAsync(done, new { projectId = made, reflection = "My first basket." }, Ct))).GetProperty("steps")[0];
            (sent.GetProperty("state").GetString(), sent.GetProperty("evidenceProjectId").GetGuid()).ShouldBe(("submitted", made));

            // Staff see the project and its picture, though the project itself was never sent to them.
            JsonElement waiting = (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/submissions"), Ct))).EnumerateArray().Single();
            JsonElement evidence = waiting.GetProperty("evidence");
            (evidence.GetProperty("title").GetString(), evidence.GetProperty("pictures")[0].GetProperty("mediaId").GetGuid()).ShouldBe(("A woven basket", media));
            (await admin.GetAsync(Relative($"api/v1/org/{org}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            (await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps/{step}/review",
                new { enrollmentId = waiting.GetProperty("enrollmentId").GetGuid(), approve = true }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await admin.GetAsync(Relative($"api/v1/org/{org}/pictures/{media}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "checked, it isn't waiting any more");
            JsonElement mine = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/playlists"), Ct))).EnumerateArray().Single(p => p.GetProperty("playlistId").GetGuid() == playlist);
            (mine.GetProperty("steps")[0].GetProperty("state").GetString(), mine.GetProperty("completedAt").ValueKind).ShouldBe(("completed", JsonValueKind.String));
        }
    }
}
