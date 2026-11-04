using System.Net;
using System.Text.Json;
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
/// A family's check-in in the app (D76), rendered from a stand-in API: their activities with the
/// meeting now or next, their attendance, and a check-in code drawn as a QR that names nobody and
/// is taken away when it runs out.
/// </summary>
public sealed class CheckInScreenTests
{
    private const string Kofi = "e0e00000-0000-0000-0000-0000000000a1";
    private const string Ama = "e0e00000-0000-0000-0000-0000000000a2";
    private const string Esi = "e0e00000-0000-0000-0000-0000000000a3";
    private const string Token = "Zm9yLXRlc3RzLW9ubHktbm90LWEtcmVhbC1jb2RlLTA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Activities = $$"""
        [{"enrollmentId":"{{Kofi}}","memberFirstName":"Kofi","listingId":"0b0b0000-0000-0000-0000-0000000000b1","listingTitle":"Junior Rowing",
          "meetingStartsAt":"2026-11-04T20:00:00Z","meetingEndsAt":"2026-11-04T21:30:00Z","meetingNow":true},
         {"enrollmentId":"{{Ama}}","memberFirstName":"Ama","listingId":"0b0b0000-0000-0000-0000-0000000000b2","listingTitle":"Chess Club",
          "meetingStartsAt":"2026-11-06T21:00:00Z","meetingEndsAt":"2026-11-06T22:00:00Z","meetingNow":false},
         {"enrollmentId":"{{Esi}}","memberFirstName":"Esi","listingId":"0b0b0000-0000-0000-0000-0000000000b3","listingTitle":"Summer Art",
          "meetingStartsAt":null,"meetingEndsAt":null,"meetingNow":false}]
        """;

    private const string History = """
        [{"enrollmentId":"e0e00000-0000-0000-0000-0000000000a1","memberFirstName":"Kofi","listingId":"0b0b0000-0000-0000-0000-0000000000b1","listingTitle":"Junior Rowing",
          "occurrenceStartsAt":"2026-11-02T20:00:00Z","state":"late","checkedInAt":"2026-11-02T20:20:00Z","checkedOutAt":null},
         {"enrollmentId":"e0e00000-0000-0000-0000-0000000000a2","memberFirstName":"Ama","listingId":"0b0b0000-0000-0000-0000-0000000000b2","listingTitle":"Chess Club",
          "occurrenceStartsAt":"2026-10-30T21:00:00Z","state":"excused","checkedInAt":null,"checkedOutAt":null}]
        """;

    private static string Code(int validForSeconds) =>
        $$"""{"token":"{{Token}}","expiresAt":"2026-11-04T20:02:00Z","validForSeconds":{{validForSeconds}}}""";

    private static FakeApi SignedIn() => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me("adult"))
        .Json(HttpMethod.Get, "me/capabilities", FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]")
        .Json(HttpMethod.Get, "me/attendance/activities", Activities);

    [Fact]
    public async Task The_screen_lists_each_activity_with_its_meeting_and_the_attendance_recorded()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/attendance", History);
        string html = await RenderAsync<CheckIn>(api);
        string shown = Shown(html);

        shown.ShouldContain("Junior Rowing");
        shown.ShouldContain("For Kofi");
        shown.ShouldContain("On now, until");
        shown.ShouldContain("Next: ");
        shown.ShouldContain("No meetings in the next two weeks.");
        shown.ShouldContain($"<a class=\"button primary\" href=\"check-in/{Kofi}\">Show check-in code</a>", Case.Sensitive, "the one meeting now is the one to tap");
        shown.ShouldContain($"<a class=\"button\" href=\"check-in/{Ama}\">");
        shown.ShouldContain("<strong>Late</strong>");
        shown.ShouldContain("<strong>Excused</strong>");
        shown.ShouldNotContain("app.checkin");
        shown.ShouldNotContain("app.attendance");
    }

    [Fact]
    public async Task A_young_person_already_checked_in_is_told_so_and_not_pressed_for_a_code()
    {
        const string checkedIn = $$"""
            [{"enrollmentId":"{{Kofi}}","memberFirstName":"Kofi","listingId":"0b0b0000-0000-0000-0000-0000000000b1","listingTitle":"Junior Rowing",
              "occurrenceStartsAt":"2026-11-04T20:00:00Z","state":"present","checkedInAt":"2026-11-04T20:03:00Z","checkedOutAt":null}]
            """;
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/attendance", checkedIn);
        string shown = Shown(await RenderAsync<CheckIn>(api));

        shown.ShouldContain("<strong>This meeting: Present</strong>");
        shown.ShouldNotContain("On now, until");
        shown.ShouldContain($"<a class=\"button\" href=\"check-in/{Kofi}\">", Case.Sensitive, "still there if staff need a code again");
        shown.ShouldNotContain("button primary");
    }

    [Fact]
    public async Task A_family_with_nothing_yet_is_told_what_will_show_here()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/attendance/activities", "[]").Json(HttpMethod.Get, "me/attendance", "[]");
        string shown = Shown(await RenderAsync<CheckIn>(api));

        shown.ShouldContain("Nothing to check in to yet.");
        shown.ShouldContain("Nothing recorded yet.");
    }

    [Fact]
    public async Task A_code_is_drawn_as_a_qr_labelled_for_its_young_person_and_never_shown_as_text()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Post, "me/attendance/badges", Code(120));
        string html = await RenderAsync<CheckInCode>(api, new Dictionary<string, object?> { ["EnrollmentId"] = Guid.Parse(Kofi) });
        string shown = Shown(html);

        shown.ShouldContain("Kofi at Junior Rowing");
        shown.ShouldMatch("<svg class=\"qr\" viewBox=\"0 0 41 41\" role=\"img\" aria-label=\"Check-in code for Kofi\"");
        shown.ShouldContain("Show this to staff when you arrive. It works once.");
        shown.ShouldMatch(@"1:5\d left", "two minutes, less the margin for the trip from the server");
        html.ShouldNotContain(Token, Case.Sensitive, "the code is only ever the QR");
        shown.ShouldNotContain("app.checkin");
    }

    [Fact]
    public async Task A_code_that_has_run_out_is_taken_away_with_a_way_to_get_a_new_one()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Post, "me/attendance/badges", Code(4));
        string html = await RenderAsync<CheckInCode>(api, new Dictionary<string, object?> { ["EnrollmentId"] = Guid.Parse(Kofi) });

        html.ShouldNotContain("<svg");
        html.ShouldContain("This code has run out.");
        html.ShouldContain(">Show a new code</button>");
    }

    [Fact]
    public async Task A_code_the_server_refuses_is_a_problem_not_a_blank_page()
    {
        using FakeApi api = SignedIn().On(HttpMethod.Post, "me/attendance/badges", _ => FakeApi.Problem(HttpStatusCode.NotFound, "checkin.enrolment"));
        string shown = Shown(await RenderAsync<CheckInCode>(api, new Dictionary<string, object?> { ["EnrollmentId"] = Guid.NewGuid() }));

        shown.ShouldContain("role=\"alert\"");
        shown.ShouldContain("That young person isn't enrolled in this activity.");
        shown.ShouldNotContain("<svg");
    }

    [Fact]
    public async Task Check_in_is_reached_from_the_account_menu_and_asks_for_the_enrolment_it_names()
    {
        string? body = null;
        using FakeApi api = SignedIn().On(HttpMethod.Post, "me/attendance/badges", r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeApi.Respond(HttpStatusCode.Created, Code(120));
        });
        (await RenderAsync<Account>(api)).ShouldContain("href=\"check-in\"");

        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();
        CheckinCode code = (await app.GetRequiredService<BrightTimeApi>().CheckinBadgeAsync(Guid.Parse(Ama), Ct)).Value!;

        using JsonDocument request = JsonDocument.Parse(body!);
        request.RootElement.GetProperty("enrollmentId").GetGuid().ShouldBe(Guid.Parse(Ama));
        (code.Token, code.ValidForSeconds).ShouldBe((Token, 120));
    }

    [Fact]
    public void A_qr_has_its_quiet_zone_and_finder_patterns_where_a_scanner_looks()
    {
        (int size, string path) = QrDrawing.Draw(Token);

        size.ShouldBe(41, "43 characters at level M need version 4 (33 modules), plus four on each side");
        // Each finder pattern's top edge is a run of seven, four modules in from the edge.
        path.ShouldStartWith("M4 4h7v1h-7z");
        path.ShouldContain("M30 4h7v1h-7z");
        path.ShouldContain("M4 30h7v1h-7z");
        path.ShouldNotContain("M0 ", Case.Sensitive, "nothing in the quiet zone");
    }

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static async Task<string> RenderAsync<TComponent>(FakeApi api, IReadOnlyDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        await using ServiceProvider app = api.Services(s => s.AddSingleton<NavigationManager, TestNavigation>());
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();
        await using HtmlRenderer renderer = new(app, app.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await renderer.RenderComponentAsync<TComponent>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(new Dictionary<string, object?>(parameters)));
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
