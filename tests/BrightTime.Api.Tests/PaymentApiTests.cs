using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Fees and payments (M3b, D55): an organisation sets fees and sliding-scale tiers; a family is
/// offered the tiers its household fits and pays one; admins record money received or waive a fee
/// with a reason; a fee a payment refers to is kept.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the fourth organisation with no staff (MessagingApiTests
/// takes the third); its admin decides and manages payments. Families are fresh accounts.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class PaymentApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Listing()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(3).First();
        return (unstaffed.First(l => l.OrgId == org).Id, org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private static async Task<HttpResponseMessage> PostWithKeyAsync(HttpClient client, string path, object body, string? key)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request, Ct);
    }

    private static string Key() => "pay-" + Guid.NewGuid().ToString("N");

    /// <summary>An accepted application for a child of the family.</summary>
    private static async Task<Guid> AcceptedAsync(HttpClient family, HttpClient admin, Guid org, Guid listing, string child)
    {
        string dob = DateTime.UtcNow.AddYears(-9).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await family.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = child, lastName = "Mensah", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Guid application = (await JsonAsync(await PostWithKeyAsync(family, "api/v1/applications", new { listingId = listing, memberId = member }, Key()), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await JsonAsync(await family.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        return application;
    }

    [Fact]
    public async Task A_family_pays_the_tier_its_household_fits_and_admins_settle_or_waive()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Listing();
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        using HttpClient family = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient stranger = await SignedInAsync(await api.NewAdultAsync());
        string fees = $"api/v1/org/{org}/listings/{listing}/fees";

        // The fees: a season with two sliding-scale tiers, and a small per-session one.
        object tiered = new
        {
            label = "Summer session",
            amountCents = 40000,
            cadence = "seasonal",
            isDefault = true,
            tiers = new object[]
            {
                new { label = "Up to 3 people, under $35,000", amountCents = 10000, householdSizeMin = 1, householdSizeMax = 3, incomeBand = "under_35k" },
                new { label = "4 or more people", amountCents = 15000, householdSizeMin = 4, householdSizeMax = 30 },
            },
        };
        object lateFee = new { label = "Late pick-up", amountCents = 500, cadence = "per_session", isDefault = false };
        await ProblemAsync(await admin.PutAsJsonAsync(fees, new { fees = new[] { tiered, new { label = "Other", amountCents = 1, cadence = "monthly", isDefault = true } } }, Ct),
            HttpStatusCode.BadRequest, "fees.count");
        object tooDear = new { label = "Cheap", amountCents = 100, cadence = "monthly", isDefault = true, tiers = new[] { new { label = "More than the fee", amountCents = 200 } } };
        await ProblemAsync(await admin.PutAsJsonAsync(fees, new { fees = new[] { tooDear } }, Ct), HttpStatusCode.BadRequest, "fees.tier");
        JsonElement set = await JsonAsync(await admin.PutAsJsonAsync(fees, new { fees = new[] { tiered, lateFee } }, Ct));
        Guid season = set.EnumerateArray().Single(f => f.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        Guid small = set[0].GetProperty("tiers").EnumerateArray().Single(t => t.GetProperty("amountCents").GetInt32() == 10000).GetProperty("id").GetGuid();
        Guid large = set[0].GetProperty("tiers").EnumerateArray().Single(t => t.GetProperty("amountCents").GetInt32() == 15000).GetProperty("id").GetGuid();

        // A household of three is offered the tier it fits, and only that one.
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 3 }, Ct));
        Guid first = await AcceptedAsync(family, admin, org, listing, "Akosua");
        JsonElement quote = await JsonAsync(await family.GetAsync(new Uri($"api/v1/applications/{first}/fees", UriKind.Relative), Ct));
        quote.GetProperty("householdSize").GetInt32().ShouldBe(3);
        quote.GetProperty("fees").EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == season).GetProperty("tiers")
            .EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ShouldBe([small]);

        // Paying: a key is needed, the tier must fit, and asking again returns the open payment.
        await ProblemAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = season, tierId = small }, null),
            HttpStatusCode.BadRequest, "application.idempotency_key");
        await ProblemAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = season, tierId = large }, Key()),
            HttpStatusCode.BadRequest, "payments.tier");
        JsonElement paying = await JsonAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = season, tierId = small }, Key()),
            HttpStatusCode.Created);
        Guid payment = paying.GetProperty("id").GetGuid();
        (paying.GetProperty("amountCents").GetInt32(), paying.GetProperty("provider").GetString(), paying.GetProperty("status").GetString())
            .ShouldBe((10000, "manual", "requires_action"));
        (await JsonAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = season }, Key()), HttpStatusCode.Created))
            .GetProperty("id").GetGuid().ShouldBe(payment);

        // The admin records the cash, once.
        (await JsonAsync(await admin.GetAsync(new Uri($"api/v1/org/{org}/listings/{listing}/payments", UriKind.Relative), Ct))).EnumerateArray()
            .Any(p => p.GetProperty("id").GetGuid() == payment).ShouldBeTrue();
        (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{payment}/received", new { reason = "Cash at sign-up" }, Ct))).GetProperty("status").GetString().ShouldBe("succeeded");
        await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{payment}/received", new { reason = "Again" }, Ct), HttpStatusCode.Conflict, "payments.settled");
        (await JsonAsync(await family.GetAsync(new Uri($"api/v1/payments/intents/{payment}", UriKind.Relative), Ct))).GetProperty("status").GetString().ShouldBe("succeeded");
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/payments/intents/{payment}", UriKind.Relative), Ct), HttpStatusCode.NotFound, "payments.not_found");

        // A second child's full fee is waived, with a reason that is kept.
        Guid second = await AcceptedAsync(family, admin, org, listing, "Kojo");
        Guid waived = (await JsonAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = second, feeScheduleId = season }, Key()), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{waived}/waive", new { reason = " " }, Ct), HttpStatusCode.BadRequest, "payments.reason");
        (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{waived}/waive", new { reason = "Scholarship" }, Ct))).GetProperty("waivedReason").GetString()
            .ShouldBe("Scholarship");
        await ProblemAsync(await family.PostAsJsonAsync($"api/v1/org/{org}/payments/{waived}/waive", new { reason = "Please" }, Ct), HttpStatusCode.Forbidden, "payments.forbidden");
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'enrollment.payment.waived' and subject_ids = array[@p] and detail->>'reason' = 'Scholarship'",
            ("p", waived))).ShouldBe(1);

        // A fee a payment refers to can't be dropped, but it can be changed.
        await ProblemAsync(await admin.PutAsJsonAsync(fees, new { fees = new[] { new { label = "New season", amountCents = 45000, cadence = "seasonal", isDefault = true } } }, Ct),
            HttpStatusCode.Conflict, "fees.in_use");
        JsonElement changed = await JsonAsync(await admin.PutAsJsonAsync(fees, new { fees = new[] { new { id = season, label = "Summer session", amountCents = 42000, cadence = "seasonal", isDefault = true } } }, Ct));
        (changed.GetArrayLength(), changed[0].GetProperty("amountCents").GetInt32()).ShouldBe((1, 42000));
        (await api.ScalarAsync<int>("select amount_cents from enrollment.payment_intent where id = @p", ("p", payment))).ShouldBe(10000, "a payment keeps its own amount");
    }
}
