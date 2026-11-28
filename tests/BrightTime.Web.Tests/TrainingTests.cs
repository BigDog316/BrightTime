using System.Net;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Workforce.Contracts;
using BrightTime.Modules.Workforce.Training;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// Training (D130): the county's organiser writes a course unit by unit, publishes it, which fixes
/// its units, and requires it of mentors; a mentor takes it, unit by unit, and the last completes
/// it until a date; search counts them trained only then; a completion that runs out starts again
/// from the next unit marked; a required course isn't retired; staff don't write courses, and a
/// young person doesn't take staff training.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class TrainingTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task The_county_requires_a_course_that_a_mentor_takes_unit_by_unit()
    {
        RequireDatabase();
        SeedOrganisation home = db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)
            .First(o => o.Status == "approved" && db.Data.Users.Any(u => u.Email == $"staff@{o.Slug}.example.org"));
        string suffix = YouthEnrolment.Letters();
        string slug = "safeguarding-" + suffix;

        // The organiser begins a course, and writes it: two units, and an empty row that's dropped.
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        (await organiser.GetAsync("/organiser/training")).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldNotContain("courses.");
        string location = (await organiser.PostAsync("/organiser/training", "create", ("title", "Safeguarding basics " + suffix), ("slug", slug)))
            .Headers.Location!.PathAndQuery;
        location.ShouldEndWith("?done=created");
        string editor = location[..location.IndexOf('?', StringComparison.Ordinal)];
        Guid course = Guid.Parse(editor[(editor.LastIndexOf('/') + 1)..]);

        (await organiser.GetAsync(editor)).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldNotContain("courses.");
        (string, string)[] details =
        [
            ("title", "Safeguarding basics " + suffix), ("slug", slug), ("summary", "What every adult needs to know."), ("provider", "Kettle County"),
            ("delivery", CourseDeliveries.SelfPaced), ("hours", "1.5"), ("validity", "12"),
        ];
        (await organiser.PostAsync(editor, "course", [.. details,
                ("unit-title-0", "Why it matters"), ("unit-body-0", "### Why\n- Children tell **someone** they trust."), ("unit-link-0", ""), ("unit-minutes-0", "10"),
                ("unit-title-1", "Reporting"), ("unit-body-1", ""), ("unit-link-1", "http://example.org/reporting"), ("unit-minutes-1", "20"),
                ("unit-title-2", ""), ("unit-body-2", ""), ("unit-link-2", ""), ("unit-minutes-2", "")]))
            .StatusCode.ShouldBe(HttpStatusCode.OK, "a link that isn't https is refused");
        Shown(organiser.Html).ShouldContain("A unit's link starts with https://.");
        (await organiser.PostAsync(editor, "course", [.. details,
                ("unit-title-0", "Why it matters"), ("unit-body-0", "### Why\n- Children tell **someone** they trust."), ("unit-link-0", ""), ("unit-minutes-0", "10"),
                ("unit-title-1", "Reporting"), ("unit-body-1", ""), ("unit-link-1", "https://example.org/reporting"), ("unit-minutes-1", "20"),
                ("unit-title-2", ""), ("unit-body-2", ""), ("unit-link-2", ""), ("unit-minutes-2", "")]))
            .Headers.Location!.PathAndQuery.ShouldBe($"{editor}?done=saved");
        (await db.ScalarAsync<long>("select count(*) from workforce.course_unit where course_id = @c", ("c", course))).ShouldBe(2);

        // Published, its units are fixed; requiring it of mentors, with no grace.
        await organiser.GetAsync(editor);
        (await organiser.PostAsync(editor, "publish")).Headers.Location!.PathAndQuery.ShouldBe($"{editor}?done=published");
        await organiser.GetAsync(editor);
        organiser.Html.ShouldNotContain("unit-title-0");
        Guid organiserId = SeedIds.For("user/" + Organiser);
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, organiserId))
        {
            (await scope.ServiceProvider.GetRequiredService<Training>().SaveAsync(course,
                    new CourseInput(slug, "Safeguarding basics " + suffix, null, null, null, null, 12, [new CourseUnitInput("More", "Text", null, null)]), Ct))
                .Error!.Code.ShouldBe("training.fixed");
        }

        await organiser.GetAsync("/organiser/training");
        (await organiser.PostAsync("/organiser/training", "require", ("role", TrainingRoles.Mentor), ("course", course.ToString()), ("blocking", "true"), ("grace", "0")))
            .Headers.Location!.PathAndQuery.ShouldBe("/organiser/training?done=required");

        // A mentor sees it required, and isn't counted trained yet.
        string email = $"trainee-{suffix}@example.org";
        Guid mentor = SeedIds.For("user/" + email);
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email, email_confirmed,
                                           security_stamp, concurrency_stamp, account_kind)
            values (@u, @t, @e, upper(@e), @e, upper(@e), true, 'stamp', 'concurrency', 'adult');
            insert into identity.app_user_role (user_id, role_id) select @u, r.id from identity.app_role r where r.tenant_id = @t and r.name = 'mentor';
            insert into directory.org_membership (id, tenant_id, org_id, user_id, org_role, status, joined_at)
            values (gen_random_uuid(), @t, @o, @u, 'mentor', 'active', now());
            select 1;
            """,
            ("u", mentor), ("t", db.Data.TenantId), ("e", email), ("o", home.Id));
        OrganisationMembership membership = new(new OrganisationId(home.Id), new UserId(mentor), "mentor", DateTimeOffset.UtcNow);
        Assert.False(await TrainedAsync(membership));

        using Browser trainee = db.NewBrowser();
        await trainee.SignInAsync(email);
        (await trainee.GetAsync("/training")).StatusCode.ShouldBe(HttpStatusCode.OK);
        trainee.Html.ShouldNotContain("training.");
        string catalogue = Shown(trainee.Html);
        catalogue.ShouldContain("You're missing training the county requires.");
        catalogue.ShouldContain("Safeguarding basics " + suffix);

        // Unit by unit: the first is done, the last completes the course for twelve months.
        string page = "/training/" + slug;
        (await trainee.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        trainee.Html.ShouldNotContain("training.");
        trainee.Html.ShouldContain("<strong>someone</strong>");
        trainee.Html.ShouldContain("https://example.org/reporting");
        List<Guid> units = await UnitsAsync(course);
        (await trainee.PostAsync(page, "unit-" + units[0])).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=unit");
        await trainee.GetAsync(page);
        (await trainee.PostAsync(page, "unit-" + units[1])).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=completed");
        (await db.ScalarAsync<bool>(
                "select completed_at is not null and expires_on = (current_date + interval '12 months')::date from workforce.course_enrollment where course_id = @c and user_id = @u",
                ("c", course), ("u", mentor)))
            .ShouldBeTrue();
        await trainee.GetAsync("/training");
        Shown(trainee.Html).ShouldContain("Your training is up to date.");
        Assert.True(await TrainedAsync(membership));

        // Run out: not trained, and marking a unit starts the course again.
        await db.ScalarAsync<int>("update workforce.course_enrollment set expires_on = current_date - 1 where course_id = @c and user_id = @u; select 1;", ("c", course), ("u", mentor));
        Assert.False(await TrainedAsync(membership));
        await trainee.GetAsync(page);
        Shown(trainee.Html).ShouldContain("Mark a unit done to take the course again.");
        (await trainee.PostAsync(page, "unit-" + units[0])).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=unit");
        (await db.ScalarAsync<bool>("select completed_at is null and expires_on is null from workforce.course_enrollment where course_id = @c and user_id = @u", ("c", course), ("u", mentor)))
            .ShouldBeTrue();
        (await db.ScalarAsync<long>(
                "select count(*) from workforce.course_unit_progress p join workforce.course_enrollment e on e.id = p.course_enrollment_id where e.course_id = @c and e.user_id = @u",
                ("c", course), ("u", mentor)))
            .ShouldBe(1);

        // A required course isn't retired; once the requirement goes, it is.
        await organiser.GetAsync(editor);
        (await organiser.PostAsync(editor, "retire")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("The county requires this course of someone");
        await organiser.GetAsync("/organiser/training");
        (await organiser.PostAsync("/organiser/training", $"remove-{TrainingRoles.Mentor}-{course}"))
            .Headers.Location!.PathAndQuery.ShouldBe("/organiser/training?done=removed");
        await organiser.GetAsync(editor);
        (await organiser.PostAsync(editor, "retire")).Headers.Location!.PathAndQuery.ShouldBe($"{editor}?done=retired");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action like 'workforce.%' and @c = any(subject_ids)", ("c", course)))
            .ShouldBeGreaterThanOrEqualTo(8);

        // Staff don't write the county's courses; a young person doesn't take staff training.
        using Browser staff = db.NewBrowser();
        await staff.SignInAsync($"staff@{home.Slug}.example.org");
        HttpResponseMessage refused = await staff.GetAsync("/organiser/training");
        refused.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        refused.Headers.Location!.ToString().ShouldContain("/access-denied");
        Guid youth = await YouthEnrolment.AccountAsync(db, "trainee-youth");
        await using (AsyncServiceScope scope = YouthEnrolment.As(db, youth))
        {
            (await scope.ServiceProvider.GetRequiredService<Training>().CatalogueAsync(new UserId(youth), Ct)).Error!.Code.ShouldBe("training.adults_only");
        }
    }

    private async Task<bool> TrainedAsync(OrganisationMembership membership)
    {
        await using AsyncServiceScope scope = YouthEnrolment.As(db, SeedIds.For("user/" + Organiser));
        return (await scope.ServiceProvider.GetRequiredService<Training>().TrainedMentorsAsync([membership], Ct)).Contains(membership.UserId.Value);
    }

    private async Task<List<Guid>> UnitsAsync(Guid course)
    {
        await using AsyncServiceScope scope = YouthEnrolment.As(db, SeedIds.For("user/" + Organiser));
        CourseView view = (await scope.ServiceProvider.GetRequiredService<Training>().GetAsync(course, Ct))!;
        return [.. view.Units.Select(u => u.Id)];
    }
}
