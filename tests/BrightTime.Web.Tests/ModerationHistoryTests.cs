using System.Net;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Moderation;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The moderation trail read back (D116): a message held, released, reported, escalated with its
/// removal, and the escalation closed with its outcome, rebuilt in order with who decided each step
/// and their note; a message screening let through says so; the county's recent decisions list
/// it; an escalation waits in the queue until its outcome is recorded, and not without one. Nobody
/// of the organisation reads its own message's history.
/// </summary>
/// <remarks>The provider is the third-last approved one with staff besides its owner; the young person is the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ModerationHistoryTests(ConsoleFixture db)
{
    private const string Queue = "/organiser/moderation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_messages_whole_moderation_story_is_rebuilt_in_order_with_who_decided_and_why()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(2).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/admin@{provider.Slug}.example.org");
        Guid moderatorId = SeedIds.For("user/" + KettleAccounts.ModeratorEmail);
        Guid youth = await YouthEnrolment.AccountAsync(db, "history");
        await db.ScalarAsync<int>("update identity.youth_capability set can_be_messaged_by_org = true where user_id = @u; select 1;", ("u", youth));
        Guid enrolment = await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, "Abena", linked: true);

        // Written: one let through, one held.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Guid welcome, held;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Conversations conversations = scope.ServiceProvider.GetRequiredService<Conversations>();
            Result<ConversationView> started = await conversations.StartAsync(new OrganisationId(provider.Id), new UserId(staff), enrolment, null, "Club " + suffix, $"Welcome, Abena ({suffix}).", Ct);
            started.IsSuccess.ShouldBeTrue(started.Error?.Code);
            welcome = await db.ScalarAsync<Guid>("select id from community.message where conversation_id = @c", ("c", started.Value.Id));
            Result<MessageView> sent = await conversations.SendAsync(new UserId(staff), started.Value.Id, $"Text me at 914-555-0102 ({suffix}).", Ct);
            sent.Value.State.ShouldBe("held");
            held = sent.Value.Id;
        }

        // Released by the moderator; reported by the young person; taken on and escalated, removed too.
        Guid report;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, moderatorId))
        {
            (await scope.ServiceProvider.GetRequiredService<HeldMessages>().ReleaseAsync(new UserId(moderatorId), held, "The club's own phone, agreed with the family.", Ct)).IsSuccess.ShouldBeTrue();
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, youth))
        {
            report = (await scope.ServiceProvider.GetRequiredService<Reports>().ReportMessageAsync(new UserId(youth), held, "grooming_concern", "He keeps texting me at night.", Ct)).Value.Id;
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, moderatorId))
        {
            Reports reports = scope.ServiceProvider.GetRequiredService<Reports>();
            (await reports.AcknowledgeAsync(new UserId(moderatorId), report, Ct)).IsSuccess.ShouldBeTrue();
            (await reports.EscalateAsync(new UserId(moderatorId), report, "Called the county's child protection line.", removeMessage: true, Ct)).IsSuccess.ShouldBeTrue();
        }

        // The escalation waits in the queue for its outcome; not closed without one.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        await moderator.GetAsync(Queue);
        moderator.Html.ShouldNotContain("moderation.");
        Shown(moderator.Html).ShouldContain("Called the county's child protection line.");
        moderator.Html.ShouldContain($"/organiser/moderation/history?message={held:D}");
        string close = "close-" + report.ToString("N");
        (await moderator.PostAsync(Queue, close, ("outcome", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Write how it ended in up to 1,000 characters.");
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, close, ("outcome", "Agency reference CP-4471; they're following it up with the family.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=escalation_closed");
        await moderator.GetAsync(Queue);
        moderator.Html.ShouldNotContain(close, Case.Sensitive, "closed, it leaves the list");
        (await db.ScalarAsync<string>("select state || '/' || resolution from community.report where id = @r", ("r", report)))
            .ShouldBe("actioned/Agency reference CP-4471; they're following it up with the family.");

        // The whole story, in order, with who and why.
        (await moderator.GetAsync($"/organiser/moderation/history?message={held:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.");
        string story = Shown(moderator.Html);
        string mod = KettleAccounts.ModeratorEmail;
        string[] steps =
        [
            "Held by screening: a phone number",
            $"Released by {mod}",
            "The club's own phone, agreed with the family.",
            "Reported by the young person: Grooming concern, Critical",
            "He keeps texting me at night.",
            $"Taken on by {mod}",
            $"Escalated by {mod}",
            $"Removed by {mod}",
            $"Escalation closed by {mod}",
            "Agency reference CP-4471",
        ];
        int at = 0;
        foreach (string step in steps)
        {
            int next = story.IndexOf(step, at, StringComparison.Ordinal);
            next.ShouldBeGreaterThan(-1, $"after the step before: {step}");
            at = next + step.Length;
        }

        story.ShouldContain($"Text me at 914-555-0102 ({suffix}).");
        story.ShouldContain("Removed", Case.Sensitive, "its state now");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and @m = any(subject_ids)", ("a", ModerationHistory.Read), ("m", held)))
            .ShouldBeGreaterThan(0);

        // A message screening let through says so.
        await moderator.GetAsync($"/organiser/moderation/history?message={welcome:D}");
        Shown(moderator.Html).ShouldContain("Screened and let through");

        // The county's recent decisions list it, with who decided.
        await moderator.GetAsync("/organiser/moderation/history");
        moderator.Html.ShouldContain($"/organiser/moderation/history?message={held:D}");
        Shown(moderator.Html).ShouldContain("Agency reference CP-4471");

        // Nobody of the organisation reads its own message's story.
        foreach (Guid colleague in new[] { staff, owner })
        {
            await using AsyncServiceScope scope = YouthEnrolment.As(db, colleague);
            (await scope.ServiceProvider.GetRequiredService<ModerationHistory>().ForMessageAsync(new UserId(colleague), held, Ct))
                .Error!.Code.ShouldBe("moderation.own_organisation");
        }

        (await moderator.GetAsync($"/organiser/moderation/history?message={Guid.NewGuid():D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
