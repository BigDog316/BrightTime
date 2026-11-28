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
/// The showcase from the app (D124): a young person reads what their organisation proposed, says
/// yes, and it's on the public showcase under their portfolio's name, which anyone reads without
/// signing in, never kept by a cache; they take it down and it's gone. Nobody else answers for
/// them. The app's client does the same.
/// </summary>
/// <remarks>
/// The proposal is made in the tables, as the console would make it, for an approved project of
/// the young person's about an activity of the first approved organisation. The young person is a
/// fresh 15-year-old who registered from the app, so consents for themselves.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ShowcaseApiTests(ApiFixture api)
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

    /// <summary>A 15-year-old who registered from the app, with a confirmed address.</summary>
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

        // As Identity computes it for a teenager without a guardian link: they may publish to the showcase.
        await api.ExecuteAsync(
            """
            insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_publish_showcase) values (@u, @t, false, true, true)
            on conflict (user_id) do update set can_publish_showcase = true
            """, ("u", id), ("t", api.Data.TenantId));
        return (email, id);
    }

    /// <summary>An approved project of the teenager's about one of the organisation's activities, proposed for the showcase.</summary>
    private async Task<Guid> ProposedAsync(Guid teen, string title)
    {
        Guid org = api.Data.Organisations.First(o => o.Status == "approved").Id;
        Guid staff = api.Data.Users.First(u => u.Memberships.Any(m => m.OrgId == org)).Id;
        Guid item = Guid.NewGuid();
        await api.ExecuteAsync(
            """
            insert into learning.participation_record (id, tenant_id, user_id, org_id, title_snapshot, started_on, source)
            values (gen_random_uuid(), @t, @u, @o, 'The activity', current_date - 30, 'provider_entry');
            with p as (
              insert into learning.project (id, tenant_id, user_id, title, description, status, approved_by, approved_at)
              values (gen_random_uuid(), @t, @u, @title, 'What I made.', 'approved', @s, now())
              returning id)
            insert into community.showcase_item (id, tenant_id, org_id, author_user_id, project_id, title, body, org_name, caption, approved_by)
            select @i, @t, @o, @u, p.id, @title, 'What I made.', 'The organisation', 'From the spring session.', @s from p
            """,
            ("t", api.Data.TenantId), ("u", teen), ("o", org), ("title", title), ("s", staff), ("i", item));
        return item;
    }

    [Fact]
    public async Task A_young_person_says_yes_and_their_work_is_on_the_public_showcase_until_they_take_it_down()
    {
        RequireDatabase();
        (string email, Guid teen) = await NewTeenAsync();
        (string otherEmail, _) = await NewTeenAsync();
        string title = $"A kite ({Guid.NewGuid():N})";
        Guid item = await ProposedAsync(teen, title);
        Guid second = await ProposedAsync(teen, title + " again");
        using HttpClient anonymous = api.Client();
        using HttpClient mine = await SignedInAsync(email);
        using HttpClient other = await SignedInAsync(otherEmail);

        (await anonymous.GetAsync(Relative("api/v1/me/showcase"), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        JsonElement listed = await JsonAsync(await mine.GetAsync(Relative("api/v1/me/showcase"), Ct));
        JsonElement waiting = listed.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == item);
        waiting.GetProperty("state").GetString().ShouldBe("waiting");
        waiting.GetProperty("caption").GetString().ShouldBe("From the spring session.");
        waiting.GetProperty("authorName").ValueKind.ShouldBe(JsonValueKind.Null, "staff's view of names isn't the author's");

        // A name first; then yes. Someone else can't answer for them.
        await ProblemAsync(await mine.PostAsJsonAsync($"api/v1/me/showcase/{item}/answer", new { show = true }, Ct), HttpStatusCode.BadRequest, "showcase.display_name");
        (await mine.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Kit", visibility = "private" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProblemAsync(await other.PostAsJsonAsync($"api/v1/me/showcase/{item}/answer", new { show = true }, Ct), HttpStatusCode.NotFound, "showcase.item_not_found");
        JsonElement shown = await JsonAsync(await mine.PostAsJsonAsync($"api/v1/me/showcase/{item}/answer", new { show = true }, Ct));
        (shown.GetProperty("state").GetString(), shown.GetProperty("displayName").GetString()).ShouldBe(("shown", "Kit"));
        (await api.ScalarAsync<bool>("select not guardian_managed and guardian_consent_id is null from community.showcase_item where id = @i", ("i", item)))
            .ShouldBeTrue("a teenager without a guardian link consents for themselves");

        // Anyone reads it, and nothing keeps a copy.
        HttpResponseMessage page = await anonymous.GetAsync(Relative("api/v1/public/showcase"), Ct);
        page.Headers.CacheControl!.NoStore.ShouldBeTrue();
        JsonElement found = (await JsonAsync(page)).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == item);
        (found.GetProperty("title").GetString(), found.GetProperty("displayName").GetString(), found.GetProperty("kind").GetString())
            .ShouldBe((title, "Kit", "project"));
        found.TryGetProperty("authorUserId", out _).ShouldBeFalse();

        // The app's client: the second, answered no; the first, taken down.
        await using ServiceProvider app = new ServiceCollection().AddLogging()
            .AddBrightTimeApp(new AppEndpoints(new Uri("https://kettle.localhost/api/v1/"), new Uri("https://kettle.localhost/")), _ => api.AppClient())
            .BuildServiceProvider();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppApi.AppSession>().SignInAsync(email, await api.SetPasswordAsync(email), Ct)).ShouldBeNull();
        AppApi.BrightTimeApi client = app.GetRequiredService<AppApi.BrightTimeApi>();
        (await client.AnswerShowcaseAsync(second, show: false, Ct)).Value!.State.ShouldBe("declined");
        (await client.TakeDownShowcaseAsync(item, Ct)).Problem.ShouldBeNull();
        IReadOnlyList<AppApi.ShowcaseEntry> after = (await client.ShowcaseAsync(Ct)).Value!;
        after.Single(e => e.Id == item).ShouldSatisfyAllConditions(e => e.State.ShouldBe("taken_down"), e => e.EndedByAuthor.ShouldBeTrue());
        after.Single(e => e.Id == second).State.ShouldBe("declined");

        (await JsonAsync(await anonymous.GetAsync(Relative("api/v1/public/showcase"), Ct))).GetProperty("items").EnumerateArray()
            .Any(e => e.GetProperty("id").GetGuid() == item).ShouldBeFalse();
        await ProblemAsync(await mine.PostAsync(Relative($"api/v1/me/showcase/{item}/take-down"), null, Ct), HttpStatusCode.Conflict, "showcase.ended");
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'showcase.public_read' and @i = any(subject_ids)", ("i", item))).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Anyone_reports_a_showcase_item_or_a_shown_portfolio_a_few_times_an_hour()
    {
        RequireDatabase();
        (string email, Guid teen) = await NewTeenAsync();
        string title = $"A boat ({Guid.NewGuid():N})";
        Guid item = await ProposedAsync(teen, title);
        await api.ExecuteAsync("update community.showcase_item set display_name = 'Kit', published_at = now() where id = @i", ("i", item));
        string slug = "kit-" + ApiFixture.Letters();
        await api.ExecuteAsync(
            "insert into learning.learner_profile (user_id, tenant_id, display_name, visibility, public_slug) values (@u, @t, 'Kit', 'public', @s)",
            ("u", teen), ("t", api.Data.TenantId), ("s", slug));
        using HttpClient anonymous = api.Client();

        // Not signed in: no reporter kept.
        JsonElement made = await JsonAsync(await anonymous.PostAsJsonAsync("api/v1/public/reports",
            new { targetKind = "showcase_item", targetId = item, reason = "hate_speech", details = "A slur in the caption." }, Ct), HttpStatusCode.Created);
        made.GetProperty("severity").GetString().ShouldBe("high");
        (await api.ScalarAsync<bool>("select reporter_user_id is null from community.report where id = @r", ("r", made.GetProperty("id").GetGuid()))).ShouldBeTrue();
        (await JsonAsync(await anonymous.PostAsJsonAsync("api/v1/public/reports", new { targetKind = "portfolio", slug, reason = "spam" }, Ct), HttpStatusCode.Created))
            .GetProperty("reason").GetString().ShouldBe("spam");
        await ProblemAsync(await anonymous.PostAsJsonAsync("api/v1/public/reports", new { targetKind = "portfolio", slug = "nobody-here", reason = "spam" }, Ct),
            HttpStatusCode.NotFound, "portfolio.not_shown");
        await ProblemAsync(await anonymous.PostAsJsonAsync("api/v1/public/reports", new { targetKind = "message", targetId = Guid.NewGuid(), reason = "spam" }, Ct),
            HttpStatusCode.BadRequest, "reports.target");

        // Signed in, through /reports as well, as themselves; never of their own.
        using HttpClient author = await SignedInAsync(email);
        await ProblemAsync(await author.PostAsJsonAsync("api/v1/reports", new { targetKind = "showcase_item", targetId = item, reason = "spam" }, Ct),
            HttpStatusCode.NotFound, "showcase.not_shown");

        // A few an hour from one address (ten by default), then refused.
        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 12 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using HttpResponseMessage response = await anonymous.PostAsJsonAsync("api/v1/public/reports", new { targetKind = "nothing", reason = "spam" }, Ct);
            last = response.StatusCode;
        }

        last.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
