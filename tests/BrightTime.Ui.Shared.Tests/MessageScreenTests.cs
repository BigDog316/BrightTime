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
/// Messages in the app (D75), rendered from a stand-in API: the family's conversations, a thread
/// that always says who reads it, an announcement that takes no reply, and what a reply sends.
/// </summary>
public sealed class MessageScreenTests
{
    private const string Talk = "c0c00000-0000-0000-0000-0000000000c1";
    private const string Notice = "c0c00000-0000-0000-0000-0000000000c2";
    private const string Org = "0a0a0000-0000-0000-0000-00000000000a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string List = $$"""
        [{"id":"{{Notice}}","orgId":"{{Org}}","orgName":"Kettle Rowing","kind":"org_broadcast","subject":"Rain plan","isOrgVisible":true,
          "includesMinor":false,"lastMessageAt":"2026-11-02T15:00:00Z","unread":2,"closedAt":null},
         {"id":"{{Talk}}","orgId":"{{Org}}","orgName":"Kettle Rowing","kind":"org_to_family","subject":null,"isOrgVisible":true,
          "includesMinor":true,"lastMessageAt":"2026-11-01T15:00:00Z","unread":0,"closedAt":null}]
        """;

    private static string Thread(string id, string kind, bool readByOrganisation, string roles, string messages) => $$"""
        {"id":"{{id}}","orgId":"{{Org}}","orgName":"Kettle Rowing","kind":"{{kind}}","subject":"Welcome","readByOrganisation":{{(readByOrganisation ? "true" : "false")}},
         "roles":{{roles}},"messages":{{messages}}}
        """;

    private const string WithYoungPerson = """
        [{"id":"e1000000-0000-0000-0000-000000000001","fromMe":false,"senderRole":"staff","body":"Hello Kofi.\nBring water.","state":"sent","sentAt":"2026-11-01T14:00:00Z"},
         {"id":"e1000000-0000-0000-0000-000000000002","fromMe":true,"senderRole":"youth","body":"Thanks!","state":"sent","sentAt":"2026-11-01T14:30:00Z"},
         {"id":"e1000000-0000-0000-0000-000000000003","fromMe":false,"senderRole":"org_admin_observer","body":"Welcome from all of us.","state":"sent","sentAt":"2026-11-01T15:00:00Z"}]
        """;

    private static FakeApi SignedIn(string kind = "adult", string? capabilities = null) => new FakeApi()
        .Json(HttpMethod.Get, "public/tenant", FakeApi.Tenant)
        .Json(HttpMethod.Post, "auth/token", FakeApi.Tokens)
        .Json(HttpMethod.Get, "me", FakeApi.Me(kind))
        .Json(HttpMethod.Get, "me/capabilities", capabilities ?? FakeApi.Capabilities)
        .Json(HttpMethod.Get, "me/saved-items", "[]");

    [Fact]
    public async Task The_list_says_who_each_conversation_is_from_and_what_is_new()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "conversations", List);
        string shown = Shown(await RenderAsync<Messages>(api));

        shown.ShouldContain("Announcement from Kettle Rowing");
        shown.ShouldContain("From Kettle Rowing");
        shown.ShouldContain("Rain plan");
        shown.ShouldContain("No subject");
        shown.ShouldContain("2 new");
        shown.ShouldContain($"href=\"messages/{Talk}\"");
        shown.ShouldContain("<li class=\"card conversation unread\"", Case.Sensitive, "the whole card opens it, and a new message marks it");
        shown.ShouldNotContain("app.messages");
    }

    [Fact]
    public async Task A_young_person_with_no_consent_for_messages_is_told_why_there_are_none()
    {
        using FakeApi api = SignedIn("youth").Json(HttpMethod.Get, "conversations", "[]");
        string shown = Shown(await RenderAsync<Messages>(api));

        shown.ShouldContain("No messages yet.");
        shown.ShouldContain("Programs can't send you messages yet.");
    }

    [Fact]
    public async Task A_thread_with_a_young_person_says_who_reads_it_and_names_staff_by_their_organisation()
    {
        using FakeApi api = SignedIn("youth").Json(HttpMethod.Get, $"conversations/{Talk}",
            Thread(Talk, "org_to_family", readByOrganisation: true, """["org_admin_observer","staff","youth"]""", WithYoungPerson));
        string html = await RenderAsync<MessageThread>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Talk) });
        string shown = Shown(html);

        shown.ShouldContain("Who's in this conversation");
        shown.ShouldContain("<li>You</li>");
        shown.ShouldContain("<li>Staff of Kettle Rowing</li>");
        shown.ShouldContain("<li>An admin of Kettle Rowing, reading along</li>");
        shown.IndexOf("Staff of", StringComparison.Ordinal).ShouldBeLessThan(shown.IndexOf("An admin of Kettle Rowing, reading", StringComparison.Ordinal), "who writes, then who reads along");
        shown.ShouldContain("A young person is in this conversation, so an admin of Kettle Rowing reads it too.");
        shown.ShouldContain("Kettle Rowing staff ·");
        shown.ShouldContain("Kettle Rowing admin ·");
        shown.ShouldContain("<li class=\"mine\"");
        shown.ShouldContain("<p class=\"body\" dir=\"auto\">Hello Kofi.\nBring water.</p>", Case.Sensitive, "line breaks are kept and shown by the stylesheet");
        html.ShouldMatch("<button type=\"button\" class=\"primary\" disabled[^>]*>Send</button>", "nothing typed yet");
        shown.ShouldNotContain("app.messages");
    }

    [Fact]
    public async Task A_held_message_is_marked_for_its_sender()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, $"conversations/{Talk}", Thread(Talk, "org_to_family", readByOrganisation: false, """["guardian","staff"]""",
            """[{"id":"e1000000-0000-0000-0000-000000000009","fromMe":true,"senderRole":"guardian","body":"Call me.","state":"held","sentAt":"2026-11-01T14:00:00Z"}]"""));
        string shown = Shown(await RenderAsync<MessageThread>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Talk) }));

        shown.ShouldContain("You ·");
        shown.ShouldContain("Held for a moderator; only you can see it");
        shown.ShouldNotContain("reads it too", Case.Sensitive, "no young person, no observer");
    }

    [Fact]
    public async Task An_announcement_takes_no_reply()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, $"conversations/{Notice}", Thread(Notice, "org_broadcast", readByOrganisation: false, """["guardian","staff"]""",
            """[{"id":"e1000000-0000-0000-0000-000000000004","fromMe":false,"senderRole":"staff","body":"We meet indoors tomorrow.","state":"sent","sentAt":"2026-11-02T15:00:00Z"}]"""));
        string html = await RenderAsync<MessageThread>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Notice) });
        string shown = Shown(html);

        shown.ShouldContain("Announcement from Kettle Rowing");
        shown.ShouldContain("Families can't reply, and nobody sees who else got it.");
        CountOf(shown, "reply").ShouldBe(1, "said once, not again under the message");
        html.ShouldNotContain("<textarea");
        html.ShouldNotContain(">Send</button>");
    }

    [Fact]
    public async Task A_conversation_the_person_is_not_in_is_a_problem_not_a_blank_page()
    {
        using FakeApi api = SignedIn().On(HttpMethod.Get, $"conversations/{Talk}", _ => FakeApi.Problem(HttpStatusCode.NotFound, "messaging.not_found"));
        string shown = Shown(await RenderAsync<MessageThread>(api, new Dictionary<string, object?> { ["Id"] = Guid.Parse(Talk) }));

        shown.ShouldContain("role=\"alert\"");
        shown.ShouldContain("You aren't in a conversation like that.");
    }

    [Fact]
    public async Task Messages_are_reached_from_the_account_menu_and_the_inbox()
    {
        using FakeApi api = SignedIn().Json(HttpMethod.Get, "me/notifications", "[]");
        (await RenderAsync<Account>(api)).ShouldContain("href=\"messages\"");
        (await RenderAsync<Notifications>(api)).ShouldContain("href=\"messages\"");
    }

    [Fact]
    public async Task A_reply_sends_its_text_and_reads_back_whether_it_was_held()
    {
        string? body = null;
        using FakeApi api = SignedIn().On(HttpMethod.Post, $"conversations/{Talk}/messages", r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeApi.Respond(HttpStatusCode.Created,
                """{"id":"e1000000-0000-0000-0000-000000000005","fromMe":true,"senderRole":"guardian","body":"See you Tuesday.","state":"sent","sentAt":"2026-11-03T12:00:00Z"}""");
        });
        await using ServiceProvider app = api.Services();
        await app.GetRequiredService<AppState>().StartAsync(Ct);
        (await app.GetRequiredService<AppSession>().SignInAsync("parent@example.net", "pw", Ct)).ShouldBeNull();

        ConversationMessage sent = (await app.GetRequiredService<BrightTimeApi>().SendMessageAsync(Guid.Parse(Talk), "See you Tuesday.", Ct)).Value!;

        using JsonDocument request = JsonDocument.Parse(body!);
        request.RootElement.GetProperty("body").GetString().ShouldBe("See you Tuesday.");
        (sent.FromMe, sent.IsHeld).ShouldBe((true, false));
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
