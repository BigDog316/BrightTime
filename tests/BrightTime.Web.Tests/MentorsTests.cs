using System.Net;
using BrightTime.Modules.Workforce.Mentors;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Mentors (D129): an organisation's admin makes a member a mentor from the staff page, and they get
/// a mentor's permissions; the mentor writes their profile and free times and lists it; another
/// organisation's staff find them only once every clearance the county requires is current, by
/// skill and day; a clearance that lapses takes them out; someone who isn't a mentor writes no
/// profile.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class MentorsTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    /// <summary>A term of the county's taxonomy, named in English: the seed has no skills or languages.</summary>
    private async Task<Guid> TermAsync(string vocabulary, string code, string name)
    {
        Guid id = UuidV7.NewId();
        await db.ScalarAsync<int>(
            """
            insert into platform.taxonomy_term (id, tenant_id, vocabulary, code) values (@id, @t, @v, @c);
            insert into platform.taxonomy_term_name (term_id, locale, name) values (@id, 'en', @n);
            select 1;
            """, ("id", id), ("t", db.Data.TenantId), ("v", vocabulary), ("c", code), ("n", name));
        return id;
    }

    [Fact]
    public async Task An_organisation_makes_a_mentor_who_is_found_only_while_cleared()
    {
        RequireDatabase();
        List<SeedOrganisation> providers = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)
            .Where(o => o.Status == "approved" && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org")).Take(2)];
        SeedOrganisation home = providers[0];
        SeedOrganisation searching = providers[1];
        string suffix = YouthEnrolment.Letters();
        string email = $"mentor-{suffix}@example.org";
        Guid mentor = SeedIds.For("user/" + email);
        Guid membership = UuidV7.NewId();
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                                           security_stamp, concurrency_stamp, account_kind)
            values (@u, @t, @e, upper(@e), @e, upper(@e), true, 'stamp', 'concurrency', 'adult');
            insert into directory.org_membership (id, tenant_id, org_id, user_id, org_role, status, joined_at)
            values (@m, @t, @o, @u, 'staff', 'active', now());
            select 1;
            """,
            ("u", mentor), ("t", db.Data.TenantId), ("e", email), ("m", membership), ("o", home.Id));

        // The owner makes them a mentor on the staff page: the membership, and a mentor's permissions.
        string staffPage = $"/provider/orgs/{home.Id}/staff";
        using Browser admin = db.NewBrowser();
        await admin.SignInAsync($"admin@{home.Slug}.example.org");
        (await admin.GetAsync(staffPage)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.PostAsync(staffPage, "role-" + membership.ToString("N"), ("membership", membership.ToString()), ("role", "mentor")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{staffPage}?done=role");
        (await db.ScalarAsync<string>("select org_role from directory.org_membership where id = @m", ("m", membership))).ShouldBe("mentor");
        (await db.ScalarAsync<long>("select count(*) from identity.app_user_role ur join identity.app_role r on r.id = ur.role_id where ur.user_id = @u and r.name = 'mentor'", ("u", mentor)))
            .ShouldBe(1);

        Guid skill = await TermAsync("skill", "bike-repair-" + suffix, "Bike repair");
        Guid otherSkill = await TermAsync("skill", "pottery-" + suffix, "Pottery");
        string language = "tw-" + suffix;
        await TermAsync("language", language, "Twi");

        // They write their profile and free times, and list it; until they're cleared, nobody finds them.
        using Browser mentorBrowser = db.NewBrowser();
        await mentorBrowser.SignInAsync(email);
        const string profile = "/mentor/profile";
        (await mentorBrowser.GetAsync(profile)).StatusCode.ShouldBe(HttpStatusCode.OK);
        mentorBrowser.Html.ShouldNotContain("mentor.profile.");
        Shown(mentorBrowser.Html).ShouldContain(home.DisplayName);
        (await mentorBrowser.PostAsync(profile, "profile", ("name", $"Ruth ({suffix})"), ("headline", "Bikes and bike repair"), ("bio", "Thirty years fixing bikes."),
                ("skill", skill.ToString()), ("language", language), ("neighbourhood", ""), ("virtual", "true"), ("hours", "4"), ("listed", "true")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{profile}?done=saved");
        await mentorBrowser.GetAsync(profile);
        (await mentorBrowser.PostAsync(profile, "availability", ("from-2", "16:00"), ("to-2", "18:00")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{profile}?done=availability");
        await mentorBrowser.GetAsync(profile);
        Shown(mentorBrowser.Html).ShouldContain("nobody can find you until every clearance the county requires is current");

        string search = $"/provider/orgs/{searching.Id}/mentors";
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync($"staff@{searching.Slug}.example.org");
        (await staff.GetAsync(search)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldNotContain("provider.mentors.");
        Shown(staff.Html).ShouldNotContain($"Ruth ({suffix})");

        // Cleared by their organisation: found, by skill and day, with how to get in touch.
        await db.ScalarAsync<int>(
            """
            insert into directory.staff_clearance (id, tenant_id, user_id, org_id, clearance_kind, status, completed_on, expires_on)
            values (gen_random_uuid(), @t, @u, @o, 'background_check', 'cleared', current_date - 10, current_date + 300),
                   (gen_random_uuid(), @t, @u, @o, 'mandated_reporter_training', 'cleared', current_date - 10, current_date + 300);
            select 1;
            """, ("t", db.Data.TenantId), ("u", mentor), ("o", home.Id));
        await staff.GetAsync(search);
        string found = Shown(staff.Html);
        found.ShouldContain($"Ruth ({suffix})");
        found.ShouldContain("Bikes and bike repair");
        found.ShouldContain("Bike repair");
        found.ShouldContain("Twi");
        found.ShouldContain(email);
        await staff.GetAsync($"{search}?skill={skill}&day=2");
        Shown(staff.Html).ShouldContain($"Ruth ({suffix})");
        await staff.GetAsync($"{search}?day=3");
        Shown(staff.Html).ShouldNotContain($"Ruth ({suffix})");
        await staff.GetAsync($"{search}?skill={otherSkill}");
        Shown(staff.Html).ShouldNotContain($"Ruth ({suffix})");
        await mentorBrowser.GetAsync(profile);
        Shown(mentorBrowser.Html).ShouldContain("Organisations' staff can find you now.");

        // A clearance that lapsed overnight takes them out, with nothing for anyone to do.
        await db.ScalarAsync<int>("update directory.staff_clearance set expires_on = current_date - 1 where user_id = @u and clearance_kind = 'background_check'; select 1;", ("u", mentor));
        await staff.GetAsync(search);
        Shown(staff.Html).ShouldNotContain($"Ruth ({suffix})");

        // Someone who isn't a mentor writes no profile; staff don't open the mentor's page.
        Guid notMentor = SeedIds.For($"user/staff@{searching.Slug}.example.org");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, notMentor))
        {
            (await scope.ServiceProvider.GetRequiredService<Mentors>().SaveAsync(new UserId(notMentor),
                    new MentorProfileInput("Me", null, null, null, null, null, true, null, null, Listed: false), Ct))
                .Error!.Code.ShouldBe("mentors.not_mentor");
        }

        (await staff.GetAsync(profile)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await mentorBrowser.GetAsync(search)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a mentor doesn't search for the organisation");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'workforce.mentors.searched' and actor_org_id = @o", ("o", searching.Id)))
            .ShouldBeGreaterThan(0);
    }
}
