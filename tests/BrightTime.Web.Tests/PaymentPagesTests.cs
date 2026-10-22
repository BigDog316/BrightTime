using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Family;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.Seed;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.DependencyInjection;
using DecisionService = BrightTime.Modules.Enrollment.Decisions.Decisions;

namespace BrightTime.Web.Tests;

/// <summary>
/// The provider console's fees and payments (D61): fees and price levels set a form at a time,
/// payments marked received or waived with a reason, a fee in use kept, and only owners and
/// admins of the organisation allowed.
/// </summary>
/// <remarks>
/// The provider is the third approved organisation from sorted index 52 on with a published
/// builtin listing (ApplicationPagesTests and AttendancePagesTests take the first two). The family
/// is the admin account at index 51 and the stranger the admin at index 50, as there.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class PaymentPagesTests(ConsoleFixture db)
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

    /// <summary>Each child applies, is accepted, and the family starts paying the listing's default fee, as their own requests would.</summary>
    private async Task<Guid[]> PayingAsync(Guid family, Guid staff, SeedOrganisation org, Guid listing, Guid fee, params string[] children)
    {
        List<Guid> applied = [];
        await using (AsyncServiceScope scope = As(family))
        {
            FamilyProfiles profiles = scope.ServiceProvider.GetRequiredService<FamilyProfiles>();
            Applications applications = scope.ServiceProvider.GetRequiredService<Applications>();
            (await profiles.SaveAsync(new UserId(family), new FamilyInput(4, "en", false), Ct)).IsSuccess.ShouldBeTrue();
            foreach (string child in children)
            {
                Result<MemberView> member = await profiles.AddMemberAsync(new UserId(family), new MemberInput("youth", child, "Boateng", null, new DateOnly(2013, 3, 3), 7, null, null), Ct);
                var created = await applications.CreateAsync(new UserId(family), listing, member.Value.Id, "pay-" + Guid.NewGuid().ToString("N"), Ct);
                (await applications.SubmitAsync(new UserId(family), created.Value.View.Id, new SubmitInput(["member_names"], null), Ct)).IsSuccess.ShouldBeTrue();
                applied.Add(created.Value.View.Id);
            }
        }

        await using (AsyncServiceScope scope = As(staff))
        {
            DecisionService decisions = scope.ServiceProvider.GetRequiredService<DecisionService>();
            (await decisions.SetSeatsAsync(new OrganisationId(org.Id), listing, 20, null, Ct)).IsSuccess.ShouldBeTrue();
            foreach (Guid application in applied)
            {
                (await decisions.DecideAsync(new OrganisationId(org.Id), new UserId(staff), application, DecisionService.Accept, null, Ct)).IsSuccess.ShouldBeTrue();
            }
        }

        List<Guid> payments = [];
        await using (AsyncServiceScope scope = As(family))
        {
            PaymentIntents intents = scope.ServiceProvider.GetRequiredService<PaymentIntents>();
            foreach (Guid application in applied)
            {
                Result<PaymentView> paying = await intents.CreateAsync(new UserId(family), true, application, null, fee, null, "pay-" + Guid.NewGuid().ToString("N"), Ct);
                paying.IsSuccess.ShouldBeTrue(paying.IsSuccess ? null : paying.Error.Code);
                payments.Add(paying.Value.Id);
            }
        }

        return [.. payments];
    }

    [Fact]
    public async Task Admins_set_fees_and_settle_or_waive_payments_and_nobody_else_can()
    {
        RequireDatabase();
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(2).First();
        Guid listing = db.Data.Listings.First(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Id;
        string fees = $"/provider/orgs/{provider.Id}/listings/{listing}/fees";
        string payments = $"/provider/orgs/{provider.Id}/listings/{listing}/payments";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync(AdminOf(provider));
        (await admin.GetAsync($"/provider/orgs/{provider.Id}/payments")).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain($"href=\"{fees}\"");

        // A fee, a price level, a refused amount, and a price level removed.
        (await admin.GetAsync(fees)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("This activity has no fees: it&#x27;s free.");
        HttpResponseMessage added = await admin.PostAsync(fees, "add", ("label", "Summer session"), ("amount", "$1,200.00"), ("cadence", "seasonal"), ("isDefault", "true"));
        added.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(added.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("Fees saved.");
        admin.Html.ShouldContain("$1,200.00");
        Guid fee = await db.ScalarAsync<Guid>("select id from enrollment.fee_schedule where listing_id = @l", ("l", listing));
        string f = fee.ToString("N");
        (await admin.PostAsync(fees, $"tier-{f}", ("fee", fee.ToString()), ("label", "Up to 3 people"), ("amount", "400"), ("min", "1"), ("max", "3"), ("band", ""))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<int>("select amount_cents from enrollment.sliding_scale_tier where fee_schedule_id = @f", ("f", fee))).ShouldBe(40000);
        await admin.GetAsync(fees);
        (await admin.PostAsync(fees, $"tier-{f}", ("fee", fee.ToString()), ("label", "Too dear"), ("amount", "lots"), ("min", ""), ("max", ""), ("band", ""))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Up to 10 price levels per fee");
        Guid tier = await db.ScalarAsync<Guid>("select id from enrollment.sliding_scale_tier where fee_schedule_id = @f", ("f", fee));
        (await admin.PostAsync(fees, $"untier-{tier:N}", ("fee", fee.ToString()), ("tier", tier.ToString()))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<long>("select count(*) from enrollment.sliding_scale_tier where fee_schedule_id = @f", ("f", fee))).ShouldBe(0);

        // Two families paying in person: one marked received, one waived with its reason kept.
        Guid[] paying = await PayingAsync(SeedIds.For("user/" + AdminOf(sorted[51])), SeedIds.For("user/" + AdminOf(provider)), provider, listing, fee, "Kwame", "Efua");
        (await admin.GetAsync(payments)).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("Kwame Boateng");
        admin.Html.ShouldContain("Efua Boateng");
        admin.Html.ShouldContain("Waiting to be paid");
        HttpResponseMessage received = await admin.PostAsync(payments, $"received-{paying[0]:N}", ("payment", paying[0].ToString()), ("note", "Cash at the desk"));
        received.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await admin.GetAsync(received.Headers.Location!.PathAndQuery);
        admin.Html.ShouldContain("Payment marked as received.");
        (await admin.PostAsync(payments, $"waive-{paying[1]:N}", ("payment", paying[1].ToString()), ("reason", "Scholarship"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await db.ScalarAsync<string>("select status from enrollment.payment_intent where id = @p", ("p", paying[0]))).ShouldBe("succeeded");
        (await db.ScalarAsync<string>("select waived_reason from enrollment.payment_intent where id = @p and status = 'waived'", ("p", paying[1]))).ShouldBe("Scholarship");
        await admin.GetAsync(payments);
        admin.Html.ShouldContain("Scholarship");

        // A fee a payment uses can't be removed.
        await admin.GetAsync(fees);
        (await admin.PostAsync(fees, $"remove-{f}", ("fee", fee.ToString()))).StatusCode.ShouldBe(HttpStatusCode.OK);
        admin.Html.ShouldContain("A payment uses a fee you removed.");

        // Another organisation's admin gets none of it.
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync(AdminOf(sorted[50]));
        (await stranger.GetAsync($"/provider/orgs/{provider.Id}/payments")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(fees)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stranger.GetAsync(payments)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        stranger.Html.ShouldNotContain("Boateng");
    }
}
