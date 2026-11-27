using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BrightTime.Ui.Shared;
using BrightTime.Ui.Shared.State;
using Microsoft.Extensions.DependencyInjection;
using AppApi = BrightTime.Ui.Shared.Api;

namespace BrightTime.Api.Tests;

/// <summary>
/// Spaces from the app (D120): a young person enrolled in an activity whose space has two staff is
/// in it, reads it, posts and replies; what they write is screened, and a held post shows only to
/// them, with why; they take down their own; staff reply and are never named to them; nobody else
/// reads or writes it; the app's client reads all of it.
/// </summary>
/// <remarks>
/// Uses the last builtin listing of the fifth organisation with staff and more than one such
/// listing; its space is made in the tables, as the console would make it. The young person is a
/// fresh 15-year-old who registered from the app.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class SpacesApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private (Guid Listing, Guid Org, string Slug) StaffedListing()
    {
        Dictionary<Guid, string> staff = api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).ToDictionary(u => u.Memberships.Single().OrgId, u => u.Email);
        var listing = api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && staff.ContainsKey(l.OrgId) && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .GroupBy(l => l.OrgId).Where(g => g.Count() > 1).Skip(4).First().Last();
        return (listing.Id, listing.OrgId, api.Data.Organisations.Single(o => o.Id == listing.OrgId).Slug);
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

    /// <summary>A 15-year-old who registered from the app, with a password and a confirmed address.</summary>
    private async Task<(string Email, Guid Id)> NewTeenAsync()
    {
        string email = $"teen-{Guid.NewGuid():N}@example.org";
        using HttpClient client = api.Client();
        string birthMonth = DateTime.UtcNow.AddYears(-15).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        (await client.PostAsJsonAsync("api/v1/auth/register", new { email, birthMonth }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Guid id = Guid.Empty;
        for (int i = 0; i < 100 && id == Guid.Empty; i++)
        {
            id = await api.ScalarAsync<long>("select count(*) from identity.app_user where normalized_email = upper(@e)", ("e", email)) > 0
                ? await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email))
                : Guid.Empty;
            if (id == Guid.Empty)
            {
                await Task.Delay(50, Ct);
            }
        }

        await api.ExecuteAsync("update identity.app_user set email_confirmed = true where id = @u", ("u", id));
        return (email, id);
    }

    [Fact]
    public async Task A_young_person_reads_and_writes_their_activitys_space_and_nobody_else_does()
    {
        RequireDatabase();
        (Guid listing, Guid org, string slug) = StaffedListing();
        (string teenEmail, Guid teenId) = await NewTeenAsync();
        using HttpClient admin = await SignedInAsync($"admin@{slug}.example.org");
        using HttpClient staff = await SignedInAsync($"staff@{slug}.example.org");
        using HttpClient teen = await SignedInAsync(teenEmail);
        using HttpClient stranger = await SignedInAsync(await api.NewAdultAsync());

        // Enrolled as herself, linked to her own account, with consent to post in a community.
        await JsonAsync(await teen.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-15).AddMonths(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await teen.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Abena", lastName = "Asante", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using (HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) })
        {
            apply.Headers.Add("Idempotency-Key", "space-" + Guid.NewGuid().ToString("N"));
            Guid application = (await JsonAsync(await teen.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await teen.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
            await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 40 }, Ct));
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        }

        await JsonAsync(await teen.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = teenId }, Ct));
        await api.ExecuteAsync(
            "insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_post_in_community) values (@u, @t, false, true, true)",
            ("u", teenId), ("t", api.Data.TenantId));

        // The activity's space, with its two staff, as the console opens it.
        Guid space = Guid.CreateVersion7();
        Guid adminId = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", $"admin@{slug}.example.org"));
        Guid staffId = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", $"staff@{slug}.example.org"));
        await api.ExecuteAsync(
            """
            insert into community.space (id, tenant_id, org_id, listing_id, space_kind, name, status) values (@s, @t, @o, @l, 'cohort', 'River club', 'active');
            insert into community.space_member (id, tenant_id, space_id, user_id, member_role, is_minor) values (@a, @t, @s, @admin, 'staff', false), (@b, @t, @s, @staff, 'staff', false);
            """,
            ("s", space), ("t", api.Data.TenantId), ("o", org), ("l", listing), ("a", Guid.CreateVersion7()), ("b", Guid.CreateVersion7()), ("admin", adminId), ("staff", staffId));

        // She's in it, and so are its staff, whom she never sees by name.
        JsonElement mine = (await JsonAsync(await teen.GetAsync(Relative("api/v1/spaces"), Ct))).EnumerateArray().Single();
        (mine.GetProperty("id").GetGuid(), mine.GetProperty("name").GetString(), mine.GetProperty("youthCount").GetInt32(), mine.GetProperty("staffCount").GetInt32())
            .ShouldBe((space, "River club", 1, 2));
        JsonElement read = await JsonAsync(await teen.GetAsync(Relative($"api/v1/spaces/{space}"), Ct));
        read.GetProperty("canPost").GetBoolean().ShouldBeTrue();
        JsonElement[] members = [.. read.GetProperty("members").EnumerateArray()];
        members.Count(m => m.TryGetProperty("userId", out _)).ShouldBe(0);
        members.Where(m => m.GetProperty("role").GetString() == "staff").ShouldAllBe(m => m.GetProperty("name").ValueKind == JsonValueKind.Null);
        members.Single(m => m.GetProperty("isMe").GetBoolean()).GetProperty("name").GetString().ShouldBe("Abena A.");

        // A hello is posted; her number is held, and only she sees it, with why.
        string suffix = ApiFixture.Letters();
        JsonElement hello = await JsonAsync(await teen.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = $"Hello from Abena ({suffix})." }, Ct), HttpStatusCode.Created);
        hello.GetProperty("state").GetString().ShouldBe("published");
        JsonElement held = await JsonAsync(await teen.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = "Text me on 914-555-0177." }, Ct), HttpStatusCode.Created);
        held.GetProperty("state").GetString().ShouldBe("held");
        held.GetProperty("heldFor").GetArrayLength().ShouldBeGreaterThan(0);
        await ProblemAsync(await teen.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = "  " }, Ct), HttpStatusCode.BadRequest, "spaces.body");

        // Staff reply to her post; the held one is nowhere in their view.
        Guid helloId = hello.GetProperty("id").GetGuid();
        await JsonAsync(await staff.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = $"Welcome, Abena ({suffix}).", replyTo = helloId }, Ct), HttpStatusCode.Created);
        string staffView = await (await staff.GetAsync(Relative($"api/v1/spaces/{space}"), Ct)).Content.ReadAsStringAsync(Ct);
        staffView.ShouldNotContain("914-555-0177");
        await ProblemAsync(await teen.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = "A reply to a reply.", replyTo = held.GetProperty("id").GetGuid() }, Ct),
            HttpStatusCode.NotFound, "spaces.reply_not_found");

        // Nobody else reads it or writes in it.
        await ProblemAsync(await stranger.GetAsync(Relative($"api/v1/spaces/{space}"), Ct), HttpStatusCode.NotFound, "spaces.not_found");
        await ProblemAsync(await stranger.PostAsJsonAsync($"api/v1/spaces/{space}/posts", new { body = "Hi all." }, Ct), HttpStatusCode.NotFound, "spaces.not_found");
        (await JsonAsync(await stranger.GetAsync(Relative("api/v1/spaces"), Ct))).GetArrayLength().ShouldBe(0);

        // She takes the held one down herself; nobody can take down someone else's.
        Guid heldId = held.GetProperty("id").GetGuid();
        await ProblemAsync(await staff.DeleteAsync(Relative($"api/v1/spaces/{space}/posts/{helloId}"), Ct), HttpStatusCode.NotFound, "spaces.post_not_found");
        (await teen.DeleteAsync(Relative($"api/v1/spaces/{space}/posts/{heldId}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.ScalarAsync<string>("select state from community.post where id = @p", ("p", heldId))).ShouldBe("deleted_by_author");

        // The app reads it through its own client and records.
        await using ServiceProvider app = new ServiceCollection().AddLogging()
            .AddBrightTimeApp(new AppEndpoints(new Uri("https://kettle.localhost/api/v1/"), new Uri("https://kettle.localhost/")), _ => api.AppClient())
            .BuildServiceProvider();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppApi.AppSession>().SignInAsync(teenEmail, await api.SetPasswordAsync(teenEmail), Ct)).ShouldBeNull();
        AppApi.BrightTimeApi client = app.GetRequiredService<AppApi.BrightTimeApi>();
        (await client.SpacesAsync(Ct)).Value!.Single().ShouldSatisfyAllConditions(s => s.Id.ShouldBe(space), s => s.IsArchived.ShouldBeFalse());
        AppApi.Space feed = (await client.SpaceAsync(space, Ct)).Value!;
        AppApi.SpacePost post = feed.Posts.Single();
        (post.Id, post.FromMe, post.IsPublished, post.Replies.Single().AuthorRole, post.Replies.Single().AuthorName).ShouldBe((helloId, true, true, "staff", (string?)null));
        feed.Members.Count(m => !m.IsYouth).ShouldBe(2);
        (await client.PostInSpaceAsync(space, $"Thanks ({suffix})!", helloId, Ct)).Value!.IsPublished.ShouldBeTrue();
    }
}
