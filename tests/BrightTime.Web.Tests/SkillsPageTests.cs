using System.Net;
using BrightTime.Seed;

namespace BrightTime.Web.Tests;

/// <summary>
/// Endorsing young people's skills in the provider console (D84): from an activity's roster, staff
/// endorse a skill at a level for a young person with their own account, change the level, and
/// withdraw it; the young person is told; someone without their own account can't hold one; and
/// nobody outside the organisation gets in.
/// </summary>
/// <remarks>
/// Every seed organisation is some web test's already. The provider is the messaging tests' (see
/// <see cref="WorkReviewPageTests"/>), on its second published builtin listing: the messaging
/// tests count only the first one's enrolled families. The young person is an account made here,
/// whose household is their own, so no seeded family changes. The stranger is the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class SkillsPageTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private async Task<Guid> TermAsync(string name)
    {
        Guid id = Guid.NewGuid();
        await db.ScalarAsync<int>(
            """
            insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values (@id, @t, 'skill', @c);
            insert into platform.taxonomy_term_name (term_id, locale, name) values (@id, 'en', @n);
            select 1;
            """,
            ("id", id), ("t", db.Data.TenantId), ("c", $"{name.ToLowerInvariant()}-{id:N}"), ("n", name));
        return id;
    }

    [Fact]
    public async Task Staff_endorse_change_and_withdraw_a_young_persons_skill_from_the_roster()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        (SeedOrganisation provider, SeedListing listing) = YouthEnrolment.SecondListing(db);
        Guid admin = SeedIds.For("user/" + AdminOf(provider));
        Guid youth = await YouthEnrolment.AccountAsync(db, "skills");
        Guid enrolled = await YouthEnrolment.EnrolAsync(db, youth, admin, provider, listing, "Kofi", linked: true);
        Guid sibling = await YouthEnrolment.EnrolAsync(db, youth, admin, provider, listing, "Esi", linked: false);
        Guid fieldwork = await TermAsync("Fieldwork");
        await TermAsync("Teamwork");
        string skills = $"/provider/orgs/{provider.Id}/listings/{listing.Id}/skills";
        string form = "endorse-" + enrolled.ToString("N");

        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));

        // The roster links here; each young person shows as the roster names them.
        (await staff.GetAsync($"/provider/orgs/{provider.Id}/listings/{listing.Id}/roster")).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain($"href=\"{skills}\"");
        (await staff.GetAsync(skills)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Kofi M");
        html.ShouldContain("Esi M");
        html.ShouldContain("They don't have an account of their own yet");
        html.ShouldContain("Fieldwork");
        html.ShouldNotContain("endorse-" + sibling.ToString("N"), Case.Sensitive, "no form for someone without their own account");
        html.ShouldNotContain("provider.skills.");

        // Without a skill chosen, nothing is endorsed.
        (await staff.PostAsync(skills, form, ("enrollment", enrolled.ToString()), ("term", ""), ("level", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        WebUtility.HtmlDecode(staff.Html).ShouldContain("Choose a skill first.");

        // Endorsed at a level, then again at another: one endorsement, at the new level.
        await staff.GetAsync(skills);
        HttpResponseMessage endorsed = await staff.PostAsync(skills, form, ("enrollment", enrolled.ToString()), ("term", fieldwork.ToString()), ("level", "2"));
        endorsed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        endorsed.Headers.Location!.PathAndQuery.ShouldBe($"{skills}?done=endorsed");
        await staff.GetAsync(skills);
        await staff.PostAsync(skills, form, ("enrollment", enrolled.ToString()), ("term", fieldwork.ToString()), ("level", "3"));
        (await db.ScalarAsync<string>("select string_agg(level::text, ',') from learning.learner_skill where user_id = @u and evidence_ref_id = @e", ("u", youth), ("e", enrolled)))
            .ShouldBe("3");
        await staff.GetAsync($"{skills}?done=endorsed");
        html = WebUtility.HtmlDecode(staff.Html);
        html.ShouldContain("Endorsed.");
        html.ShouldContain("Fieldwork</span> · Proficient");

        // The young person is told, by the organisation and the skill.
        (await db.ScalarAsync<long>(
            "select count(*) from platform.outbox_message where message_type = 'platform.notification.requested' and payload::text like @u and payload::text like '%notify.skill.endorsed%'",
            ("u", "%" + youth + "%"))).ShouldBe(2);

        // Withdrawn, it's gone; withdrawing again finds nothing.
        string withdraw = $"withdraw-{enrolled:N}-{fieldwork:N}";
        HttpResponseMessage withdrawn = await staff.PostAsync(skills, withdraw, ("enrollment", enrolled.ToString()), ("term", fieldwork.ToString()));
        withdrawn.Headers.Location!.PathAndQuery.ShouldBe($"{skills}?done=withdrawn");
        (await db.ScalarAsync<long>("select count(*) from learning.learner_skill where user_id = @u", ("u", youth))).ShouldBe(0);
        await staff.GetAsync($"{skills}?done=withdrawn");
        WebUtility.HtmlDecode(staff.Html).ShouldContain("No skills endorsed yet.");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.skill.endorsed' and actor_org_id = @o and detail->>'what' = 'withdrawn'", ("o", provider.Id)))
            .ShouldBe(1);

        // Another organisation's admin is turned away.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(skills)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Kofi");
    }
}
