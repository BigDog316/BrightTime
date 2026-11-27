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
/// Young people with their own accounts, enrolled as the family's and the staff's own requests
/// would enrol them, for the console's pages about young people's records (D84, D85).
/// </summary>
internal static class YouthEnrolment
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The messaging tests' provider (see <see cref="WorkReviewPageTests"/>) and its second published builtin listing, which they don't count.</summary>
    public static (SeedOrganisation Provider, SeedListing Listing) SecondListing(ConsoleFixture db)
    {
        List<SeedOrganisation> sorted = [.. db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal)];
        SeedOrganisation provider = Enumerable.Range(52, sorted.Count - 52).Select(i => sorted[i])
            .Where(o => o.Status == "approved" && db.Data.Listings.Any(l => l.OrgId == o.Id && l.Status == "published" && l.RegistrationMode == "builtin"))
            .Skip(5).First();
        return (provider, db.Data.Listings.Where(l => l.OrgId == provider.Id && l.Status == "published" && l.RegistrationMode == "builtin").Skip(1).First());
    }

    /// <summary>
    /// Eight random lower-case letters, to tell a test's messages apart: hex sometimes gives seven
    /// digits in a row, which the screens rightly take for a phone number and hold (D117, D118).
    /// </summary>
    public static string Letters() => string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + (c % 26))));

    public static AsyncServiceScope As(ConsoleFixture db, Guid user)
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        TenantContext tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenant.SetTenant(new TenantId(db.Data.TenantId), TimeZoneInfo.Utc, Locale.English);
        tenant.SetActor(new UserId(user), ActorKind.User);
        return scope;
    }

    /// <summary>A young person with their own account, who can sign up for themselves.</summary>
    public static async Task<Guid> AccountAsync(ConsoleFixture db, string prefix)
    {
        Guid user = UuidV7.NewId();
        string email = $"{prefix}-{user:N}@example.org";
        await db.ScalarAsync<int>(
            """
            insert into identity.app_user (id, tenant_id, user_name, normalized_user_name, email, normalized_email,
                                           security_stamp, concurrency_stamp, account_kind, age_band)
            values (@u, @t, @e, upper(@e), @e, upper(@e), 'stamp', 'concurrency', 'youth', '15_17');
            insert into identity.youth_capability (user_id, tenant_id, is_under_13, has_account_consent, can_self_enroll, recomputed_at)
            values (@u, @t, false, true, true, now());
            select 1;
            """,
            ("u", user), ("t", db.Data.TenantId), ("e", email));
        return user;
    }

    /// <summary>A member of the young person's own household applies and is accepted; the enrolment's id.</summary>
    public static async Task<Guid> EnrolAsync(ConsoleFixture db, Guid owner, Guid staff, SeedOrganisation org, SeedListing listing, string first, bool linked)
    {
        short age = listing.AgeMin ?? 14;
        DateOnly born = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-age).AddMonths(-1);
        Guid application;
        await using (AsyncServiceScope scope = As(db, owner))
        {
            FamilyProfiles profiles = scope.ServiceProvider.GetRequiredService<FamilyProfiles>();
            Applications applications = scope.ServiceProvider.GetRequiredService<Applications>();
            (await profiles.SaveAsync(new UserId(owner), new FamilyInput(2, "en", false), Ct)).IsSuccess.ShouldBeTrue();
            Result<MemberView> member = await profiles.AddMemberAsync(new UserId(owner), new MemberInput("youth", first, "Mensah", null, born, listing.GradeMin, null, null), Ct);
            member.IsSuccess.ShouldBeTrue();
            if (linked)
            {
                (await profiles.LinkAccountAsync(new UserId(owner), member.Value.Id, new UserId(owner), Ct)).IsSuccess.ShouldBeTrue();
            }

            var created = await applications.CreateAsync(new UserId(owner), listing.Id, member.Value.Id, "enrol-" + Guid.NewGuid().ToString("N"), Ct);
            created.IsSuccess.ShouldBeTrue();
            (await applications.SubmitAsync(new UserId(owner), created.Value.View.Id, new SubmitInput(["member_names"], null), Ct)).IsSuccess.ShouldBeTrue();
            application = created.Value.View.Id;
        }

        await using (AsyncServiceScope scope = As(db, staff))
        {
            DecisionService decisions = scope.ServiceProvider.GetRequiredService<DecisionService>();
            (await decisions.SetSeatsAsync(new OrganisationId(org.Id), listing.Id, 40, null, Ct)).IsSuccess.ShouldBeTrue();
            (await decisions.DecideAsync(new OrganisationId(org.Id), new UserId(staff), application, DecisionService.Accept, null, Ct)).IsSuccess.ShouldBeTrue();
        }

        return await db.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
    }
}
