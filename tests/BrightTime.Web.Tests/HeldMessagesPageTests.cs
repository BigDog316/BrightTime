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
/// The county's moderation queue (D114): staff's messages to a young person that screening held
/// are listed for a moderator with why and what came before; one released with a reason reaches
/// the young person and tells them, one removed reaches nobody and shows its sender it was removed;
/// each is a moderation record and an audit event. Nobody decides without a reason, twice, for
/// their own organisation, or into a closed conversation; a provider never opens the queue, and
/// a moderator opens nothing else of the organisers'.
/// </summary>
/// <remarks>
/// The provider is the last approved one with a member of staff besides its owner, and the young
/// person is the test's own, so the held messages are this test's alone.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class HeldMessagesPageTests(ConsoleFixture db)
{
    private const string Queue = "/organiser/moderation";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_moderator_releases_or_removes_what_screening_held_and_nobody_of_the_organisation_decides()
    {
        RequireDatabase();
        (SeedOrganisation provider, SeedListing listing) = Provider();
        string staffEmail = $"staff@{provider.Slug}.example.org";
        Guid staff = SeedIds.For("user/" + staffEmail);
        Guid owner = SeedIds.For($"user/admin@{provider.Slug}.example.org");
        Guid youth = await YouthEnrolment.AccountAsync(db, "held");
        await db.ScalarAsync<int>("update identity.youth_capability set can_be_messaged_by_org = true where user_id = @u; select 1;", ("u", youth));
        Guid enrolment = await YouthEnrolment.EnrolAsync(db, youth, owner, provider, listing, "Esi", linked: true);

        // Staff open a conversation with the young person; three tries at taking it elsewhere are held.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Guid conversation;
        Guid phone, app, third;
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, staff))
        {
            Conversations conversations = scope.ServiceProvider.GetRequiredService<Conversations>();
            Result<ConversationView> started = await conversations.StartAsync(new OrganisationId(provider.Id), new UserId(staff), enrolment, null, "Club " + suffix, "Welcome to the club, Esi.", Ct);
            started.IsSuccess.ShouldBeTrue(started.Error?.Code);
            conversation = started.Value.Id;
            phone = await HeldAsync(conversations, staff, conversation, $"Text me at 914-555-0101 ({suffix}).");
            app = await HeldAsync(conversations, staff, conversation, $"Add me on snapchat ({suffix}).");
            third = await HeldAsync(conversations, staff, conversation, $"Call me tonight ({suffix}).");
        }

        // The moderator sees them, oldest first, with why and the message before them; the young person sees none.
        using Browser moderator = db.NewBrowser();
        await moderator.SignInAsync(KettleAccounts.ModeratorEmail);
        (await moderator.GetAsync(Queue)).StatusCode.ShouldBe(HttpStatusCode.OK);
        moderator.Html.ShouldContain($"href=\"{Queue}\"", Case.Sensitive, "the console's nav links to it");
        moderator.Html.ShouldNotContain("moderation.");
        string shown = Shown(moderator.Html);
        shown.ShouldContain($"From {provider.DisplayName}");
        shown.ShouldContain($"Text me at 914-555-0101 ({suffix}).");
        shown.ShouldContain("a phone number");
        shown.ShouldContain("another messaging app");
        shown.ShouldContain("Welcome to the club, Esi.", Case.Sensitive, "the message before, for context");
        shown.IndexOf($"914-555-0101 ({suffix})", StringComparison.Ordinal).ShouldBeLessThan(shown.IndexOf($"snapchat ({suffix})", StringComparison.Ordinal), "oldest first");
        (await YouthSeesAsync(youth, conversation)).ShouldBe(["Welcome to the club, Esi."]);

        // Not without a reason.
        (await moderator.PostAsync(Queue, Form(phone), ("decision", "release"), ("reason", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("Give a reason of up to 1,000 characters.");
        (await db.ScalarAsync<string>("select state from community.message where id = @m", ("m", phone))).ShouldBe("held");

        // Released: the young person has it, and is told.
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(phone), ("decision", "release"), ("reason", "Club leader's work phone, agreed with the family.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=released");
        (await YouthSeesAsync(youth, conversation)).ShouldBe(["Welcome to the club, Esi.", $"Text me at 914-555-0101 ({suffix})."]);
        (await db.ScalarAsync<long>("select count(*) from platform.outbox_message where payload::text like @k", ("k", $"%message:{phone:N}:{youth:N}%"))).ShouldBe(1);

        // Removed: nobody sees it but its sender, marked so in the console.
        await moderator.GetAsync(Queue);
        Shown(moderator.Html).ShouldNotContain($"914-555-0101 ({suffix})", Case.Sensitive, "decided, it's off the queue");
        (await moderator.PostAsync(Queue, Form(app), ("decision", "remove"), ("reason", "Asks a young person to move to another app.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=removed");
        await moderator.GetAsync(Queue + "?done=removed");
        Shown(moderator.Html).ShouldContain("Removed. Only its sender sees it, marked as removed.");
        (await YouthSeesAsync(youth, conversation)).ShouldNotContain($"Add me on snapchat ({suffix}).");
        using (Browser sender = db.NewBrowser())
        {
            await sender.SignInAsync(staffEmail);
            (await sender.GetAsync($"/provider/orgs/{provider.Id}/messages/{conversation}")).StatusCode.ShouldBe(HttpStatusCode.OK);
            string thread = Shown(sender.Html);
            thread.ShouldContain($"Add me on snapchat ({suffix}).");
            thread.ShouldContain("Removed by a moderator; only you can see it");

            // A provider never opens the queue.
            HttpResponseMessage refused = await sender.GetAsync(Queue);
            (refused.StatusCode == HttpStatusCode.Forbidden
             || (refused.StatusCode == HttpStatusCode.Redirect && refused.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal))).ShouldBeTrue();
        }

        // Each decision is a moderation record and an audit event, as is reading the queue.
        (await db.ScalarAsync<string>(
            "select string_agg(action || ':' || (actor_user_id = @mod)::text, ',' order by occurred_at) from community.moderation_action where target_kind = 'message' and target_id = any(@ids)",
            ("mod", SeedIds.For("user/" + KettleAccounts.ModeratorEmail)), ("ids", new[] { phone, app }))).ShouldBe("publish:true,remove:true");
        foreach ((string action, Guid message) in new[] { (HeldMessages.Read, phone), (HeldMessages.Released, phone), (HeldMessages.Removed, app) })
        {
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and @m = any(subject_ids)", ("a", action), ("m", message)))
                .ShouldBeGreaterThan(0, action);
        }

        // Decided once; never by its sender's organisation; not into a closed conversation, though it can still be removed.
        Guid moderatorId = SeedIds.For("user/" + KettleAccounts.ModeratorEmail);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, moderatorId))
        {
            HeldMessages held = scope.ServiceProvider.GetRequiredService<HeldMessages>();
            (await held.ReleaseAsync(new UserId(moderatorId), app, "Second thoughts.", Ct)).Error!.Code.ShouldBe("moderation.not_held");
        }

        foreach (Guid colleague in new[] { staff, owner })
        {
            await using AsyncServiceScope scope = YouthEnrolment.As(db, colleague);
            HeldMessages held = scope.ServiceProvider.GetRequiredService<HeldMessages>();
            (await held.ReleaseAsync(new UserId(colleague), third, "It's fine.", Ct)).Error!.Code.ShouldBe("moderation.own_organisation", "the sender, then the admin reading along");
        }

        await db.ScalarAsync<int>("update community.conversation set closed_at = now() where id = @c; select 1;", ("c", conversation));
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(third), ("decision", "release"), ("reason", "Harmless."))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(moderator.Html).ShouldContain("This conversation has been closed, so the message can't be delivered. You can remove it.");
        await moderator.GetAsync(Queue);
        (await moderator.PostAsync(Queue, Form(third), ("decision", "remove"), ("reason", "Conversation closed.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{Queue}?done=removed");

        // A moderator has the queue and nothing else of the organisers'.
        HttpResponseMessage review = await moderator.GetAsync("/organiser/review");
        (review.StatusCode == HttpStatusCode.Forbidden
         || (review.StatusCode == HttpStatusCode.Redirect && review.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal))).ShouldBeTrue();
    }

    private static string Form(Guid message) => "decide-" + message.ToString("N");

    /// <summary>
    /// The last approved organisation, by link name, with a member of staff besides its owner (the
    /// second adult a conversation with a young person needs) and a published builtin listing.
    /// </summary>
    private (SeedOrganisation Provider, SeedListing Listing) Provider()
    {
        SeedOrganisation provider = db.Data.Organisations.OrderByDescending(o => o.Slug, StringComparer.Ordinal)
            .First(o => o.Status == "approved"
                && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")
                && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"));
        return (provider, db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin"));
    }

    private static async Task<Guid> HeldAsync(Conversations conversations, Guid staff, Guid conversation, string body)
    {
        Result<MessageView> sent = await conversations.SendAsync(new UserId(staff), conversation, body, Ct);
        sent.Value.State.ShouldBe("held", body);
        return sent.Value.Id;
    }

    /// <summary>The messages the young person reads in the conversation, as their own request would read them.</summary>
    private async Task<List<string>> YouthSeesAsync(Guid youth, Guid conversation)
    {
        await using AsyncServiceScope scope = YouthEnrolment.As(db, youth);
        Result<ConversationView> view = await scope.ServiceProvider.GetRequiredService<Conversations>().GetAsync(new UserId(youth), conversation, Ct);
        return [.. view.Value.Messages.Select(m => m.Body)];
    }
}
