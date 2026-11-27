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
/// Muting and suspending a person (D119): from a held message's history, a moderator mutes the
/// adult who wrote it, who then can't write to the young person or open a conversation with one,
/// lifts it early with a reason, and suspends the account, which is then suspended, its sessions
/// ended, and its end booked; each shows in the history. Not without a reason, not on a young
/// person, and not by anyone of the organisation.
/// </summary>
/// <remarks>The provider is the fourth-last approved one with staff besides its owner; the young person is the test's own.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class RestrictionTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_moderator_mutes_lifts_and_suspends_the_adult_who_wrote_it()
    {
        RequireDatabase();
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(3).First();
        SeedListing listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin");
        Guid staff = SeedIds.For($"user/staff@{provider.Slug}.example.org");
        Guid owner = SeedIds.For($"user/admin@{provider.Slug}.example.org");
        Guid youth = await YouthEnrolment.AccountAsync(db, "restrict");
        await db.ScalarAsync<int>("update identity.youth_capability set can_be_messaged_by_org = true where user_id = @u; select 1;", ("u", youth));
        Guid enrolment = await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, "Kweku", linked: true);

        string suffix = YouthEnrolment.Letters();
        Guid conversation, held, youthHeld;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Conversations conversations = scope.ServiceProvider.GetRequiredService<Conversations>();
            Result<ConversationView> started = await conversations.StartAsync(new OrganisationId(provider.Id), new UserId(staff), enrolment, null, "Club " + suffix, $"Welcome, Kweku ({suffix}).", Ct);
            started.IsSuccess.ShouldBeTrue(started.Error?.Code);
            conversation = started.Value.Id;
            held = (await conversations.SendAsync(new UserId(staff), conversation, $"Add me on snapchat ({suffix}).", Ct)).Value.Id;
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, youth))
        {
            youthHeld = (await scope.ServiceProvider.GetRequiredService<Conversations>().SendAsync(new UserId(youth), conversation, "My number is 914-555-0106.", Ct)).Value.Id;
        }

        // From the held message's history: not without a reason; then muted for 7 days.
        string history = $"/organiser/moderation/history?message={held:D}";
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync(history)).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldNotContain("moderation.");
        Shown(moderator.Html).ShouldContain("No mute or suspension so far.");
        (await moderator.PostAsync(history, "restrict", ("kind", "mute"), ("length", "7d"), ("reason", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Give a reason of up to 1,000 characters.");
        await moderator.GetAsync(history);
        (await moderator.PostAsync(history, "restrict", ("kind", "mute"), ("length", "7d"), ("reason", "Pushing a young person towards another app.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{history}&done=restricted");

        // Muted: they can't write to the young person, or open a conversation with one.
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Conversations conversations = scope.ServiceProvider.GetRequiredService<Conversations>();
            (await conversations.SendAsync(new UserId(staff), conversation, "See you Tuesday.", Ct)).Error!.Code.ShouldBe("messaging.muted");
            (await conversations.StartAsync(new OrganisationId(provider.Id), new UserId(staff), enrolment, null, null, "Hello again.", Ct)).Error!.Code.ShouldBe("messaging.muted");
        }

        await moderator.GetAsync(history);
        string shown = Shown(moderator.Html);
        shown.ShouldContain($"Muted by {KettleAccounts.ModeratorEmail} until");
        shown.ShouldContain($"Sender muted by {KettleAccounts.ModeratorEmail} until");
        Guid mute = await db.ScalarAsync<Guid>("select id from community.person_restriction where user_id = @u and kind = 'mute'", ("u", staff));
        (await db.ScalarAsync<string>("select action || '/' || duration_hours || '/' || target_kind from community.moderation_action where target_id = @u and action = 'mute'", ("u", staff)))
            .ShouldBe("mute/168/user");

        // Lifted early, with a reason: they write again.
        (await moderator.PostAsync(history, "lift-" + mute.ToString("N"), ("reason", "Spoken to by the organisation; it was a misunderstanding.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{history}&done=restriction_lifted");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            (await scope.ServiceProvider.GetRequiredService<Conversations>().SendAsync(new UserId(staff), conversation, "See you Tuesday.", Ct)).IsSuccess.ShouldBeTrue();
        }

        // Suspended for 24 hours: the account is suspended, its sessions ended, and its end booked.
        string stamp = await db.ScalarAsync<string>("select security_stamp from identity.app_user where id = @u", ("u", staff));
        await moderator.GetAsync(history);
        (await moderator.PostAsync(history, "restrict", ("kind", "suspend"), ("length", "24h"), ("reason", "Kept on after the mute was lifted.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{history}&done=restricted");
        (await db.ScalarAsync<string>("select status || '/' || (suspended_until > now() + interval '23 hours')::text || '/' || (security_stamp <> @s)::text from identity.app_user where id = @u",
            ("u", staff), ("s", stamp))).ShouldBe("suspended/true/true");
        (await db.ScalarAsync<long>("select count(*) from platform.job where job_type = 'identity.suspension_ends' and payload->>'userId' = @u and run_after > now()",
            ("u", staff.ToString()))).ShouldBe(1);
        await moderator.GetAsync(history);
        story(Shown(moderator.Html));

        void story(string page)
        {
            string mod = KettleAccounts.ModeratorEmail;
            int muted = page.IndexOf($"Sender muted by {mod}", StringComparison.Ordinal);
            int lifted = page.IndexOf($"Mute lifted by {mod}", StringComparison.Ordinal);
            int suspended = page.IndexOf($"Sender suspended by {mod}", StringComparison.Ordinal);
            muted.ShouldBeGreaterThan(-1);
            lifted.ShouldBeGreaterThan(muted, "the history has each step in order");
            suspended.ShouldBeGreaterThan(lifted);
        }

        // Lifting the suspension makes the account active again.
        Guid suspension = await db.ScalarAsync<Guid>("select id from community.person_restriction where user_id = @u and kind = 'suspend'", ("u", staff));
        (await moderator.PostAsync(history, "lift-" + suspension.ToString("N"), ("reason", "Suspended the wrong person.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{history}&done=restriction_lifted");
        (await db.ScalarAsync<string>("select status || '/' || (suspended_until is null)::text from identity.app_user where id = @u", ("u", staff))).ShouldBe("active/true");

        // Never a young person; never by anyone of the organisation.
        Guid moderatorId = SeedIds.For("user/" + KettleAccounts.ModeratorEmail);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, moderatorId))
        {
            (await scope.ServiceProvider.GetRequiredService<Restrictions>().ImposeAsync(new UserId(moderatorId), youthHeld, "mute", "24h", "Shared a number.", Ct))
                .Error!.Code.ShouldBe("moderation.adults_only");
        }

        await using (AsyncServiceScope scope = YouthEnrolment.As(db, owner))
        {
            (await scope.ServiceProvider.GetRequiredService<Restrictions>().ImposeAsync(new UserId(owner), held, "mute", "24h", "Mine to decide.", Ct))
                .Error!.Code.ShouldBe("moderation.own_organisation");
        }
    }
}
