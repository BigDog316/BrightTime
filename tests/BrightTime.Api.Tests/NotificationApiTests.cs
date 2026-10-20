using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BrightTime.Seed;

namespace BrightTime.Api.Tests;

/// <summary>Push devices, notification preferences and quiet hours, reminders and the inbox, through the API.</summary>
/// <remarks>Admins 45 to 49, one per test.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class NotificationApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    [Fact]
    public async Task A_device_is_kept_encrypted_and_a_handed_down_phone_changes_hands()
    {
        RequireDatabase();
        using HttpClient first = await SignedInAsync(Admin(45));
        using HttpClient second = await SignedInAsync(Admin(46));
        string token = "fcm-" + Guid.NewGuid().ToString("N") + "-CaseMatters";

        HttpResponseMessage made = await first.PostAsJsonAsync("api/v1/devices", new { platform = "android", token, appVersion = "1.0.0", osVersion = "15", locale = "es" }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid device = (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        // Neither the token nor any recognisable part of it is in the table.
        string sealedHex = await api.ScalarAsync<string>("select encode(token_enc, 'hex') from notify.push_device where id = @d", ("d", device));
        sealedHex.ShouldNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes("CaseMatters")));
        (await api.ScalarAsync<int>("select length(token_hash) from notify.push_device where id = @d", ("d", device))).ShouldBe(32);

        // The phone passes to someone else, who signs in on it: it is theirs now.
        HttpResponseMessage taken = await second.PostAsJsonAsync("api/v1/devices", new { platform = "android", token }, Ct);
        taken.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await taken.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid().ShouldBe(device);
        (await first.DeleteAsync(new Uri($"api/v1/devices/{device}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await second.DeleteAsync(new Uri($"api/v1/devices/{device}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage bad = await first.PostAsJsonAsync("api/v1/devices", new { platform = "pager", token = "x" }, Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:devices.platform");
    }

    [Fact]
    public async Task Preferences_default_to_on_and_hold_one_quiet_window()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(47));

        JsonElement defaults = await me.GetFromJsonAsync<JsonElement>("api/v1/me/notification-preferences", Ct);
        defaults.GetProperty("quietFrom").ValueKind.ShouldBe(JsonValueKind.Null);
        defaults.GetProperty("choices").EnumerateArray().ShouldAllBe(c => c.GetProperty("enabled").GetBoolean());

        HttpResponseMessage set = await me.PutAsJsonAsync("api/v1/me/notification-preferences", new
        {
            quietFrom = "21:30",
            quietTo = "07:00",
            choices = new[] { new { kind = "deadline_reminder", channel = "push", enabled = false } },
        }, Ct);
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        JsonElement mine = await me.GetFromJsonAsync<JsonElement>("api/v1/me/notification-preferences", Ct);
        mine.GetProperty("quietFrom").GetString().ShouldBe("21:30");
        mine.GetProperty("quietTo").GetString().ShouldBe("07:00");
        mine.GetProperty("choices").EnumerateArray()
            .Single(c => c.GetProperty("kind").GetString() == "deadline_reminder" && c.GetProperty("channel").GetString() == "push")
            .GetProperty("enabled").GetBoolean().ShouldBeFalse();

        HttpResponseMessage half = await me.PutAsJsonAsync("api/v1/me/notification-preferences", new { quietFrom = "21:30" }, Ct);
        half.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await half.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:preferences.quiet_hours");

        HttpResponseMessage sms = await me.PutAsJsonAsync("api/v1/me/notification-preferences",
            new { choices = new[] { new { kind = "deadline_reminder", channel = "sms", enabled = true } } }, Ct);
        sms.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/notifications", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_reminder_is_booked_for_the_moment_it_is_due()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(48));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid listing = await api.ScalarAsync<Guid>(
            """
            select o.listing_id from opportunity.listing_occurrence o
            join opportunity.listing l on l.id = o.listing_id and l.status = 'published'
            where o.starts_at > now() + interval '2 days' and not o.is_cancelled
            order by o.starts_at limit 1
            """);

        HttpResponseMessage made = await me.PostAsJsonAsync("api/v1/me/reminders", new { listingId = listing, anchor = "next_session", leadMinutes = 60 }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement reminder = await made.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Guid id = reminder.GetProperty("id").GetGuid();
        reminder.GetProperty("remindAt").GetDateTimeOffset().ShouldBeGreaterThan(now);

        (await api.ScalarAsync<long>("select count(*) from platform.job where unique_key = @k and state = 'ready' and run_after = @at",
            ("k", $"opportunities.reminder_due:{id:N}"), ("at", reminder.GetProperty("remindAt").GetDateTimeOffset()))).ShouldBe(1);
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/reminders", Ct)).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldContain(id);

        (await me.DeleteAsync(new Uri($"api/v1/me/reminders/{id}", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await me.GetFromJsonAsync<JsonElement>("api/v1/me/reminders", Ct)).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_reminder_that_could_never_fire_is_refused()
    {
        RequireDatabase();
        using HttpClient me = await SignedInAsync(Admin(49));
        SeedListing withoutDeadline = api.Data.Listings.First(l => l.Status == "published" && l.Deadlines.Count == 0
            && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved");

        HttpResponseMessage nothing = await me.PostAsJsonAsync("api/v1/me/reminders", new { listingId = withoutDeadline.Id, anchor = "deadline", leadMinutes = 60 }, Ct);
        nothing.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await nothing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:reminders.nothing_ahead");

        Guid soon = await api.ScalarAsync<Guid>(
            """
            select o.listing_id from opportunity.listing_occurrence o
            join opportunity.listing l on l.id = o.listing_id and l.status = 'published'
            where o.starts_at between now() + interval '1 hour' and now() + interval '5 days' and not o.is_cancelled
            order by o.starts_at limit 1
            """);
        HttpResponseMessage late = await me.PostAsJsonAsync("api/v1/me/reminders", new { listingId = soon, anchor = "next_session", leadMinutes = 43_200 }, Ct);
        late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await late.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:reminders.too_late");
    }
}
