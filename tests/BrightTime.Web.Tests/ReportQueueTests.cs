using System.Net;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Moderation;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Reports in the county's moderation queue (D115): critical first, then high, then normal, a
/// critical one marked when it has waited an hour; a moderator takes one on, escalates it with a
/// note and the message removed, dismisses one and the message stays, removes one and every
/// report of that message is settled. Each is a moderation record and an audit event; nobody of the
/// conversation's organisation decides, and nothing is decided without a note.
/// </summary>
/// <remarks>
/// The provider is the second-last approved one with a member of staff besides its owner (the
/// held-message tests take the last), and the young person is the test's own.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ReportQueueTests(ConsoleFixture db)
{
    private const string Queue = "/organiser/moderation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_moderator_takes_on_escalates_dismisses_and_removes_what_was_reported()
    {
        RequireDatabase();
        Reports.ModeratorPermission.ShouldBe(Permissions.Moderation.Triage, "who is emailed of a critical report is who may decide it");
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(1).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/admin@{provider.Slug}.example.org");
        Guid youth = await YouthEnrolment.AccountAsync(db, "reports");
        await db.ScalarAsync<int>("update identity.youth_capability set can_be_messaged_by_org = true where user_id = @u; select 1;", ("u", youth));
        Guid enrolment = await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, "Kojo", linked: true);

        string suffix = Guid.NewGuid().ToString("N")[..8];
        Guid conversation;
        Guid secret, spam, rude, other;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Conversations conversations = scope.ServiceProvider.GetRequiredService<Conversations>();
            Result<ConversationView> started = await conversations.StartAsync(new OrganisationId(provider.Id), new UserId(staff), enrolment, null, "Club " + suffix, $"Welcome, Kojo ({suffix}).", Ct);
            started.IsSuccess.ShouldBeTrue(started.Error?.Code);
            conversation = started.Value.Id;
            secret = await SentAsync(conversations, staff, conversation, $"You're my favourite. Let's keep our chats just between us ({suffix}).");
            spam = await SentAsync(conversations, staff, conversation, $"Buy raffle tickets ({suffix}).");
            rude = await SentAsync(conversations, staff, conversation, $"You're useless at this ({suffix}).");
            other = await SentAsync(conversations, staff, conversation, $"See you Tuesday ({suffix}).");
        }

        // The young person reports three; the admin reading along reports one of them too.
        Guid grooming = await ReportAsync(youth, secret, "grooming_concern", "It made me uncomfortable.");
        Guid junk = await ReportAsync(youth, spam, "spam", null);
        Guid harassment = await ReportAsync(youth, rude, "harassment", null);
        Guid alongside = await ReportAsync(owner, rude, "harassment", null);
        Guid tuesday = await ReportAsync(youth, other, "safety_other", null);
        await db.ScalarAsync<int>("update community.report set created_at = now() - interval '2 hours' where id = @r; select 1;", ("r", grooming));

        // Critical first, marked overdue; then high; then normal, oldest first.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync(Queue)).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.");
        moderator.Html.ShouldContain(">Moderation</a>", Case.Sensitive, "the nav names the whole queue now");
        string shown = Shown(moderator.Html);
        // Each report's own heading; a message also shows as context before a later one.
        int Where(Guid report) => moderator.Html.IndexOf($"id=\"report-{report:N}\"", StringComparison.Ordinal);
        shown.ShouldContain("Grooming concern");
        shown.ShouldContain("Not taken on within the hour.");
        shown.ShouldContain("It made me uncomfortable.");
        shown.ShouldContain("young person, ");
        Where(grooming).ShouldBeLessThan(Where(harassment), "critical before high");
        Where(harassment).ShouldBeLessThan(Where(junk), "high before normal");
        Where(junk).ShouldBeLessThan(Where(tuesday), "then oldest first");

        // Taken on: acknowledged, and theirs.
        Guid moderatorId = SeedIds.For("user/" + KettleAccounts.ModeratorEmail);
        (await moderator.PostAsync(Queue, "take-" + grooming.ToString("N"))).Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=taken_on");
        (await db.ScalarAsync<string>("select state || '/' || (acknowledged_at is not null)::text || '/' || (assigned_to = @m)::text from community.report where id = @r",
            ("r", grooming), ("m", moderatorId))).ShouldBe("triaged/true/true");

        // Escalated: not without a note; with one, and the message removed too.
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(grooming), ("decision", "escalate"), ("note", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Write a note of up to 1,000 characters.");
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(grooming), ("decision", "escalate"), ("remove_too", "true"),
            ("note", "Reported to the county's child protection line, case noted; the organisation's safeguarding lead told.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=escalated");
        (await db.ScalarAsync<string>("select state || '/' || (escalation_note like 'Reported to%')::text from community.report where id = @r", ("r", grooming)))
            .ShouldBe("escalated/true");

        // Dismissed: the message stays.
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(junk), ("decision", "dismiss"), ("note", "A fundraiser the families know about.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=dismissed");

        // Removed: every report of it waiting is settled.
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(harassment), ("decision", "remove"), ("note", "Belittling a young person.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=report_removed");
        (await db.ScalarAsync<string>("select string_agg(state, ',' order by created_at) from community.report where id = any(@r)", ("r", new[] { harassment, alongside })))
            .ShouldBe("actioned,actioned");

        // What the young person reads now, and what's on record.
        List<string> seen = await YouthSeesAsync(youth, conversation);
        seen.ShouldContain(_bodies[spam]);
        seen.ShouldNotContain(_bodies[secret]);
        seen.ShouldNotContain(_bodies[rude]);
        (await db.ScalarAsync<string>(
            "select string_agg(action, ',' order by action) from community.moderation_action where report_id = any(@r) and actor_user_id = @m",
            ("r", new[] { grooming, junk, harassment }), ("m", moderatorId))).ShouldBe("escalate_external,no_action,remove,remove");
        foreach ((string action, Guid report) in new[] { (Reports.Read, grooming), (Reports.Acknowledged, grooming), (Reports.Escalated, grooming), (Reports.Dismissed, junk), (Reports.Removed, harassment) })
        {
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and @r = any(subject_ids)", ("a", action), ("r", report)))
                .ShouldBeGreaterThan(0, action);
        }

        // Nobody of the organisation decides one; it's still waiting.
        foreach (Guid colleague in new[] { staff, owner })
        {
            await using AsyncServiceScope scope = YouthEnrolment.As(db, colleague);
            (await scope.ServiceProvider.GetRequiredService<Reports>().DismissAsync(new UserId(colleague), tuesday, "Nothing wrong.", Ct))
                .Error!.Code.ShouldBe("reports.own_organisation");
        }

        (await db.ScalarAsync<string>("select state from community.report where id = @r", ("r", tuesday))).ShouldBe("open");

        // And a decided one isn't decided again.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, moderatorId))
        {
            (await scope.ServiceProvider.GetRequiredService<Reports>().DismissAsync(new UserId(moderatorId), junk, "Again.", Ct))
                .Error!.Code.ShouldBe("reports.not_waiting");
        }
    }

    private static string Form(Guid report) => "report-" + report.ToString("N");

    /// <summary>Each message's text, by id, as staff sent it.</summary>
    private readonly Dictionary<Guid, string> _bodies = [];

    private async Task<Guid> SentAsync(Conversations conversations, Guid staff, Guid conversation, string body)
    {
        Result<MessageView> sent = await conversations.SendAsync(new UserId(staff), conversation, body, Ct);
        sent.Value.State.ShouldBe("sent", body);
        _bodies[sent.Value.Id] = body;
        return sent.Value.Id;
    }

    private async Task<Guid> ReportAsync(Guid reporter, Guid message, string reason, string? details)
    {
        await using AsyncServiceScope scope = YouthEnrolment.As(db, reporter);
        Result<ReportReceipt> made = await scope.ServiceProvider.GetRequiredService<Reports>().ReportMessageAsync(new UserId(reporter), message, reason, details, Ct);
        made.IsSuccess.ShouldBeTrue(made.Error?.Code);
        return made.Value.Id;
    }

    private async Task<List<string>> YouthSeesAsync(Guid youth, Guid conversation)
    {
        await using AsyncServiceScope scope = YouthEnrolment.As(db, youth);
        Result<ConversationView> view = await scope.ServiceProvider.GetRequiredService<Conversations>().GetAsync(new UserId(youth), conversation, Ct);
        return [.. view.Value.Messages.Select(m => m.Body)];
    }
}
