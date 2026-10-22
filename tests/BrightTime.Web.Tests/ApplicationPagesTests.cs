using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's applications (D59): the queue with what each family shares, places,
/// deciding several at once and one at a time, the waitlist and its offers, and who may see any
/// of it.
/// </summary>
/// <remarks>
/// The provider is the first organisation from sorted index 52 on with a published builtin
/// listing; the family is the admin account at index 51, applying as a parent; the stranger is
/// the admin at index 50. No other web test uses them.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ApplicationPagesTests(ConsoleFixture db)
{
    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private List<SeedOrganisation> Sorted() => [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    /// <summary>A family applies for each child, as the family's own requests would, sharing their names and nothing else.</summary>
    private async Task<Guid[]> ApplyAsync(Guid owner, Guid listing, params string[] children)
    {
        await using AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        TenantContext tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        tenant.SetActor(new UserId(owner), ActorKind.User);
        FamilyProfiles family = scope.ServiceProvider.GetRequiredService<FamilyProfiles>();
        Applications applications = scope.ServiceProvider.GetRequiredService<Applications>();

        (await family.SaveAsync(new UserId(owner), new FamilyInput(4, "en", false), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        List<Guid> made = [];
        foreach (string child in children)
        {
            Result<MemberView> member = await family.AddMemberAsync(new UserId(owner),
                new MemberInput("youth", child, "Owusu", null, new DateOnly(2015, 5, 5), 5, null, null), TestContext.Current.CancellationToken);
            member.IsSuccess.ShouldBeTrue(member.IsSuccess ? null : member.Error.Code);
            var created = await applications.CreateAsync(new UserId(owner), listing, member.Value.Id, "web-" + Guid.NewGuid().ToString("N"), TestContext.Current.CancellationToken);
            created.IsSuccess.ShouldBeTrue(created.IsSuccess ? null : created.Error.Code);
            Result<ApplicationView> submitted = await applications.SubmitAsync(new UserId(owner), created.Value.View.Id, new SubmitInput(["member_names"], null), TestContext.Current.CancellationToken);
            submitted.IsSuccess.ShouldBeTrue(submitted.IsSuccess ? null : submitted.Error.Code);
            made.Add(created.Value.View.Id);
        }

        return [.. made];
    }

    [Fact]
    public async Task Staff_work_through_an_activitys_applications_and_nobody_else_sees_them()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = Sorted();
        int index = Enumerable.Range(52, sorted.Count - 52).First(i =>
            db.Data.Listings.Any(l => l.OrgId == sorted[i].Id && l.Status == "published" && l.RegistrationMode == "builtin") && sorted[i].Status == "approved");
        SeedOrganisation provider = sorted[index];
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        Guid owner = SeedIds.For("user/" + AdminOf(sorted[51]));
        Guid[] applied = await ApplyAsync(owner, listing, "Ama", "Kofi");
        string queue = $"/provider/orgs/{provider.Id}/listings/{listing}/applications";

        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));

        // The activity list leads to the queue, which names each applicant as the family shared them.
        (await staff.GetAsync($"/provider/orgs/{provider.Id}/applications")).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain($"href=\"{queue}\"");
        (await staff.GetAsync(queue)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain("Ama Owusu");
        staff.Html.ShouldContain("Kofi Owusu");
        staff.Html.ShouldContain("This activity has no places set yet.");

        // One place, then both accepted at once: the first takes it, the second is left, saying why.
        HttpResponseMessage seats = await staff.PostAsync(queue, "seats", ("capacity", "1"), ("waitlistCapacity", "5"));
        seats.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(seats.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Places saved.");
        staff.Html.ShouldContain("0 of 1 places taken.");
        (await staff.PostAsync(queue, "bulk", ("ids", applied[0].ToString()), ("ids", applied[1].ToString()), ("decision", "accept"), ("note", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldContain("1 application decided.");
        staff.Html.ShouldContain("These were left as they were:");
        (await db.ScalarAsync<string>("select status from enrollment.application where id = @a", ("a", applied[0]))).ShouldBe("accepted");
        (await db.ScalarAsync<string>("select status from enrollment.application where id = @a", ("a", applied[1]))).ShouldBe("submitted");

        // The second, opened on its own: no birth date was shared, so none is shown; waitlisted with a note.
        string second = $"/provider/orgs/{provider.Id}/applications/{applied[1]}";
        (await staff.GetAsync(second)).StatusCode.ShouldBe(HttpStatusCode.OK);
        staff.Html.ShouldNotContain("Date of birth");
        HttpResponseMessage decided = await staff.PostAsync(second, "decide", ("decision", "waitlist"), ("note", "Next place that frees up"));
        decided.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(decided.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Decision saved.");
        staff.Html.ShouldContain("Waitlisted · place 1 on the waitlist");
        staff.Html.ShouldContain("Next place that frees up");

        // Filtering, then a second place offered to the waitlist.
        await staff.GetAsync($"{queue}?status=waitlisted");
        staff.Html.ShouldContain("Kofi Owusu");
        staff.Html.ShouldNotContain("Ama Owusu");
        await staff.PostAsync(queue, "seats", ("capacity", "2"), ("waitlistCapacity", "5"));
        await staff.GetAsync(queue);
        HttpResponseMessage offered = await staff.PostAsync(queue, "offer", ("count", "1"));
        offered.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(offered.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("1 family was offered a place.");
        (await db.ScalarAsync<string>("select status from enrollment.application where id = @a", ("a", applied[1]))).ShouldBe("offered");

        // Another organisation's admin sees none of it, and the refusals are on record.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(queue)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Owusu");
        (await stranger.GetAsync(second)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Owusu");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.application.org_read' and outcome = 'deny' and actor_org_id = @o",
            ("o", provider.Id))).ShouldBeGreaterThanOrEqualTo(2);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.application.org_read' and outcome = 'allow' and actor_org_id = @o and subject_ids @> array[@a]",
            ("o", provider.Id), ("a", applied[1]))).ShouldBeGreaterThan(0);
    }
}
