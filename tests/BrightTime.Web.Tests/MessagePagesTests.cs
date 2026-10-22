using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using DecisionService = BrightTime.Modules.Enrollment.Decisions.Decisions;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's messaging (D64): a conversation opened with a family from a roster,
/// answered, an announcement to an activity's families, and nobody outside the organisation let in.
/// </summary>
/// <remarks>
/// The provider is the sixth approved organisation from sorted index 52 on with a published
/// builtin listing (the applications, attendance, payments, forms and eligibility tests take the
/// first five). The family is the admin account at index 51 and the stranger the admin at index 50.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class MessagePagesTests(ConsoleFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string AdminOf(SeedOrganisation org) => $"admin@{org.Slug}.example.org";

    private AsyncServiceScope As(Guid user)
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        TenantContext tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        tenant.SetActor(new UserId(user), ActorKind.User);
        return scope;
    }

    /// <summary>A child applies and is accepted, as the family's and the staff's own requests would; the enrolment's id.</summary>
    private async Task<Guid> EnrolAsync(Guid family, Guid staff, SeedOrganisation org, Guid listing)
    {
        Guid application;
        await using (AsyncServiceScope scope = As(family))
        {
            FamilyProfiles profiles = scope.ServiceProvider.GetRequiredService<FamilyProfiles>();
            Applications applications = scope.ServiceProvider.GetRequiredService<Applications>();
            (await profiles.SaveAsync(new UserId(family), new FamilyInput(4, "en", false), Ct)).IsSuccess.ShouldBeTrue();
            Result<MemberView> member = await profiles.AddMemberAsync(new UserId(family), new MemberInput("youth", "Adwoa", "Asante", null, new DateOnly(2014, 4, 4), 6, null, null), Ct);
            var created = await applications.CreateAsync(new UserId(family), listing, member.Value.Id, "msg-" + Guid.NewGuid().ToString("N"), Ct);
            (await applications.SubmitAsync(new UserId(family), created.Value.View.Id, new SubmitInput(["member_names"], null), Ct)).IsSuccess.ShouldBeTrue();
            application = created.Value.View.Id;
        }

        await using (AsyncServiceScope scope = As(staff))
        {
            DecisionService decisions = scope.ServiceProvider.GetRequiredService<DecisionService>();
            (await decisions.SetSeatsAsync(new OrganisationId(org.Id), listing, 20, null, Ct)).IsSuccess.ShouldBeTrue();
            (await decisions.DecideAsync(new OrganisationId(org.Id), new UserId(staff), application, DecisionService.Accept, null, Ct)).IsSuccess.ShouldBeTrue();
        }

        return await db.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
    }

    [Fact]
    public async Task Staff_message_a_family_and_announce_to_an_activity_and_nobody_else_gets_in()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(5).First();
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        Guid family = SeedIds.For("user/" + AdminOf(sorted[51]));
        Guid enrolment = await EnrolAsync(family, SeedIds.For("user/" + AdminOf(provider)), provider, listing);
        string messages = $"/provider/orgs/{provider.Id}/messages";

        using Browser staff = db.NewBrowser();
        await staff.SignInAsync(AdminOf(provider));

        // A conversation opened from the roster's link, with the family in it.
        (await staff.GetAsync($"{messages}?enrollment={enrolment}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        HttpResponseMessage started = await staff.PostAsync($"{messages}?enrollment={enrolment}", "start", ("subject", "Pick-up time"), ("body", "Pick-up moves to 5 pm on Friday."));
        started.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string thread = started.Headers.Location!.AbsolutePath;
        Guid conversation = Guid.Parse(thread[(thread.LastIndexOf('/') + 1)..]);
        await staff.GetAsync(started.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Conversation started.");
        staff.Html.ShouldContain("Pick-up moves to 5 pm on Friday.");
        staff.Html.ShouldContain("In it: ");
        (await db.ScalarAsync<long>("select count(*) from community.conversation_participant where conversation_id = @c and user_id = @u and removed_at is null",
            ("c", conversation), ("u", family))).ShouldBe(1);

        // Answered on the thread.
        HttpResponseMessage replied = await staff.PostAsync(thread, "reply", ("body", "Please bring a water bottle."));
        replied.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(replied.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Message sent.");
        (await db.ScalarAsync<long>("select count(*) from community.message where conversation_id = @c", ("c", conversation))).ShouldBe(2);

        // An announcement to the activity's families: the one family gets it.
        await staff.GetAsync(messages);
        HttpResponseMessage announced = await staff.PostAsync(messages, "broadcast", ("listing", listing.ToString()), ("subject", "Closed Monday"), ("body", "No session on Monday."));
        announced.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await staff.GetAsync(announced.Headers.Location!.PathAndQuery);
        staff.Html.ShouldContain("Sent to 1 family.");
        await staff.GetAsync(messages);
        staff.Html.ShouldContain("Pick-up time");
        staff.Html.ShouldContain("Closed Monday");
        staff.Html.ShouldContain("Announcement");
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'messaging.sent' and actor_org_id = @o", ("o", provider.Id))).ShouldBeGreaterThanOrEqualTo(2);

        // Another organisation's admin gets neither the list nor the thread.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync(messages)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(thread)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("water bottle");
    }
}
