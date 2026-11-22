using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Pages;
using BrightTime.Ui.Shared.State;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Ui.Shared.Tests;

/// <summary>
/// My schedule in the app (D97), rendered from a stand-in API: offered from the account menu; an
/// offer waiting, meetings by day with whom each is for when the household has more than one
/// person going, deadlines (a draft's named by whom it's for, with a way to finish it), and a
/// calendar file handed to the share sheet; and plain words when there is nothing coming up.
/// </summary>
public sealed class ScheduleScreenTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Household = """
        {"from":"2026-11-22T12:00:00Z","until":"2026-12-20T12:00:00Z",
         "meetings":[
           {"enrollmentId":"e0000000-0000-0000-0000-000000000001","who":"Ximena","listingId":"10000000-0000-0000-0000-000000000001","listingSlug":"river-robotics","listingTitle":"River Robotics","place":"Kettle Library","startsAt":"2026-11-24T21:00:00Z","endsAt":"2026-11-24T22:30:00Z"},
           {"enrollmentId":"e0000000-0000-0000-0000-000000000002","who":"Quillon","listingId":"10000000-0000-0000-0000-000000000001","listingSlug":"river-robotics","listingTitle":"River Robotics","place":"Kettle Library","startsAt":"2026-11-24T21:00:00Z","endsAt":"2026-11-24T22:30:00Z"},
           {"enrollmentId":"e0000000-0000-0000-0000-000000000002","who":"Quillon","listingId":"10000000-0000-0000-0000-000000000003","listingSlug":"swim-club","listingTitle":"Swim Club","place":null,"startsAt":"2026-12-01T16:00:00Z","endsAt":"2026-12-01T17:00:00Z"}],
         "deadlines":[
           {"listingId":"10000000-0000-0000-0000-000000000002","listingSlug":"spring-mural","listingTitle":"Spring Mural","kind":"registration","label":null,"dueAt":"2026-11-27T16:00:00Z","applicationId":null,"who":null},
           {"listingId":"10000000-0000-0000-0000-000000000004","listingSlug":"chess-league","listingTitle":"Chess League","kind":"application","label":"Forms in by Friday","dueAt":"2026-11-28T16:00:00Z","applicationId":"a0000000-0000-0000-0000-000000000004","who":"Zebedee"}],
         "offers":[{"applicationId":"a0000000-0000-0000-0000-000000000005","who":"Odalys","listingId":"10000000-0000-0000-0000-000000000005","listingTitle":"Teen Theatre"}]}
        """;

    private const string OnePerson = """
        {"from":"2026-11-22T12:00:00Z","until":"2026-12-20T12:00:00Z",
         "meetings":[{"enrollmentId":"e0000000-0000-0000-0000-000000000002","who":"Quillon","listingId":"10000000-0000-0000-0000-000000000003","listingSlug":"swim-club","listingTitle":"Swim Club","place":"Kettle Pool","startsAt":"2026-12-01T16:00:00Z","endsAt":"2026-12-01T17:00:00Z"}],
         "deadlines":[],"offers":[]}
        """;

    private const string Nothing = """{"from":"2026-11-22T12:00:00Z","until":"2026-12-20T12:00:00Z","meetings":[],"deadlines":[],"offers":[]}""";

    private static FakeApi SignedIn(string kind = "adult") => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "consent/disclosures", "[]");

    [Fact]
    public async Task The_account_menu_offers_it()
    {
        using FakeApi api = SignedIn("youth");
        Shown(await RenderAsync<Account>(api)).ShouldContain("href=\"schedule\">My schedule</a>");
    }

    [Fact]
    public async Task A_household_sees_its_offer_meetings_by_day_and_deadlines()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/schedule", Household);
        string shown = Shown(await RenderAsync<Schedule>(api));

        shown.ShouldContain("<h1>My schedule</h1>");
        shown.ShouldContain("Waiting for your answer");
        shown.ShouldContain("Odalys has been offered a place at Teen Theatre.");
        shown.ShouldContain("href=\"applications/a0000000-0000-0000-0000-000000000005\">Answer the offer</a>");

        shown.ShouldContain("href=\"listings/river-robotics\" dir=\"auto\">River Robotics</a>");
        shown.ShouldContain("Kettle Library");
        shown.ShouldContain("For Ximena");
        shown.ShouldContain("For Quillon");
        shown.Split("<h3>").Length.ShouldBe(3, "two days with meetings, each with its heading");

        shown.ShouldContain("<h2 id=\"deadlines\">Dates to remember</h2>");
        shown.ShouldContain("Registration deadline");
        shown.ShouldContain("href=\"listings/spring-mural\" dir=\"auto\">Spring Mural</a>");
        shown.ShouldContain("Forms in by Friday");
        shown.ShouldContain("The application for Zebedee is not sent yet.");
        shown.ShouldContain("href=\"applications/a0000000-0000-0000-0000-000000000004\">Finish the application</a>");

        shown.ShouldContain(">Add to your calendar</button>");
        shown.ShouldContain("The file has no names in it.");
        shown.ShouldNotContain("app.schedule.");
        shown.ShouldNotContain("listing.deadline.");
    }

    [Fact]
    public async Task One_person_going_isnt_named_on_each_meeting()
    {
        using FakeApi api = SignedIn("youth").Json(HttpMethod.Get, "me/schedule", OnePerson);
        string shown = Shown(await RenderAsync<Schedule>(api));

        shown.ShouldContain("Swim Club");
        shown.ShouldContain("Kettle Pool");
        shown.ShouldNotContain("For Quillon");
        shown.ShouldNotContain("Waiting for your answer");
        shown.ShouldContain("No deadlines in the next four weeks for things you saved or started to apply for.");
        shown.ShouldContain(">Add to your calendar</button>");
    }

    [Fact]
    public async Task Nothing_coming_up_says_so_and_offers_no_calendar()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/schedule", Nothing);
        string shown = Shown(await RenderAsync<Schedule>(api));

        shown.ShouldContain("No meetings in the next four weeks.");
        shown.ShouldContain("No deadlines in the next four weeks");
        shown.ShouldNotContain("Add to your calendar");
    }

    [Fact]
    public async Task The_calendar_is_read_as_a_file()
    {
        using FakeApi api = SignedIn().On(HttpMethod.Get, "me/schedule/calendar", _ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("BEGIN:VCALENDAR")) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "brighttime-schedule.ics" };
            return response;
        });
        await using ServiceProvider app = await SignedInAppAsync(api);

        DownloadedFile file = (await app.GetRequiredService<BrightTimeApi>().ScheduleCalendarAsync(Ct)).Value!;

        (file.ContentType, file.FileName, Encoding.UTF8.GetString(file.Bytes)).ShouldBe(("text/calendar", "brighttime-schedule.ics", "BEGIN:VCALENDAR"));
    }

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static async Task<ServiceProvider> SignedInAppAsync(FakeApi api)
    {
        ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("sam@example.net", "pw", Ct)).ShouldBeNull();
        return app;
    }

    private static async Task<string> RenderAsync<TComponent>(FakeApi api)
        where TComponent : IComponent
    {
        await using ServiceProvider app = await SignedInAppAsync(api);
        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(ParameterView.Empty);
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://app.test/", "https://app.test/");

        protected override void NavigateToCore(string uri, NavigationOptions options) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
