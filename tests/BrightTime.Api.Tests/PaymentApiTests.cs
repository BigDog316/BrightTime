using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Payments;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// Fees and payments (M3b, D55): an organisation sets fees and sliding-scale tiers; a family is
/// offered the tiers its household fits and pays one; admins record money received or waive a fee
/// with a reason; a fee a payment refers to is kept.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the fourth organisation with no staff (MessagingApiTests
/// takes the third), and the Stripe test the fifth's; their admins decide and manage payments.
/// Families are fresh accounts.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class PaymentApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private (Guid Listing, Guid Org) Listing(int next = 0)
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        List<(Guid Id, Guid OrgId)> unstaffed = [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
        Guid org = unstaffed.Select(l => l.OrgId).Distinct().Skip(3 + next).First();
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

        // The family lists what it has paid (D73); a season paid isn't charged twice, a per-session fee may be.
        (await JsonAsync(await family.GetAsync(new Uri($"api/v1/applications/{first}/payments", UriKind.Relative), Ct))).EnumerateArray()
            .Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("status").GetString())).ShouldBe([(payment, "succeeded")]);
        await ProblemAsync(await stranger.GetAsync(new Uri($"api/v1/applications/{first}/payments", UriKind.Relative), Ct), HttpStatusCode.NotFound, "application.not_found");
        await ProblemAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = season, tierId = small }, Key()),
            HttpStatusCode.Conflict, "payments.already_paid");
        Guid late = set.EnumerateArray().Single(f => !f.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        Guid firstLate = (await JsonAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = late }, Key()), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{firstLate}/received", new { reason = "Cash" }, Ct));
        (await JsonAsync(await PostWithKeyAsync(family, "api/v1/payments/intents", new { applicationId = first, feeScheduleId = late }, Key()), HttpStatusCode.Created))
            .GetProperty("id").GetGuid().ShouldNotBe(firstLate);

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
        JsonElement changed = await JsonAsync(await admin.PutAsJsonAsync(fees, new
        {
            fees = new[]
            {
                new { id = season, label = "Summer session", amountCents = 42000, cadence = "seasonal", isDefault = true },
                new { id = late, label = "Late pick-up", amountCents = 500, cadence = "per_session", isDefault = false },
            },
        }, Ct));
        (changed.GetArrayLength(), changed[0].GetProperty("amountCents").GetInt32(), changed[0].GetProperty("isDefault").GetBoolean()).ShouldBe((2, 42000, true));
        (await api.ScalarAsync<bool>("select is_default from enrollment.fee_schedule where id = @f", ("f", season))).ShouldBeTrue("a fee changed in place stays the default");
        (await api.ScalarAsync<int>("select amount_cents from enrollment.payment_intent where id = @p", ("p", payment))).ShouldBe(10000, "a payment keeps its own amount");
    }

    private const string WebhookSecret = "whsec_api_test";

    /// <summary>Stripe, as far as this application can see it: intents made and read, or a refusal.</summary>
    private sealed class FakeStripe : HttpMessageHandler
    {
        private int _next;

        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        public bool Down { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            if (Down)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":{"type":"api_error"}}""", Encoding.UTF8, "application/json") };
            }

            string id = request.Method == HttpMethod.Get ? request.RequestUri!.Segments[^1] : $"pi_test{Interlocked.Increment(ref _next)}";
            string amount = body.Split('&').FirstOrDefault(p => p.StartsWith("amount=", StringComparison.Ordinal))?["amount=".Length..] ?? "10000";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"{{id}}","status":"requires_payment_method","amount":{{amount}},"currency":"usd","client_secret":"{{id}}_secret_abc"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Signed(string body, DateTimeOffset at)
    {
        long t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(WebhookSecret), Encoding.UTF8.GetBytes($"{t}.{body}")))}";
    }

    private static string StripeEvent(string id, string type, string data) =>
        $$"""{"id":"{{id}}","object":"event","type":"{{type}}","data":{"object":""" + data + "}}";

    private static async Task<HttpResponseMessage> CallbackAsync(HttpClient client, string provider, string body, string? signature)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"api/v1/webhooks/payments/{provider}", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            request.Headers.Add("Stripe-Signature", signature);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>Runs the queued payment events for a payment on the worker's login, as the jobs host would, and clears them.</summary>
    private async Task<int> RunPaymentEventsAsync(string providerRef)
    {
        List<(Guid Id, Guid Tenant, string Payload)> queued = [];
        await using (Npgsql.NpgsqlConnection connection = new(api.Connection))
        {
            await connection.OpenAsync(Ct);
            await using Npgsql.NpgsqlCommand select = new(
                "select id, tenant_id, payload::text from platform.job where job_type = @t and state = 'ready' and payload->>'providerRef' = @r order by id", connection);
            select.Parameters.AddWithValue("t", PaymentEventJob.JobType);
            select.Parameters.AddWithValue("r", providerRef);
            await using Npgsql.NpgsqlDataReader reader = await select.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                queued.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2)));
            }
        }

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddBrightTimeDbContext<EnrollmentDbContext>(api.JobsConnection, EnrollmentDbContext.SchemaName, EnrollmentDbContext.MigrationsHistoryTable);
        services.AddScoped<PaymentEventJob>();
        await using ServiceProvider worker = services.BuildServiceProvider();
        foreach ((Guid id, Guid tenant, string payload) in queued)
        {
            await using AsyncServiceScope scope = worker.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(tenant), TimeZoneInfo.Utc, Locale.English);
            await scope.ServiceProvider.GetRequiredService<PaymentEventJob>().ExecuteAsync(
                new JobExecutionContext(new JobRecord { Id = id, TenantId = tenant, JobType = PaymentEventJob.JobType, Payload = payload }, new TenantId(tenant), 1), Ct);
            await api.ExecuteAsync("delete from platform.job where id = @id", ("id", id));
        }

        return queued.Count;
    }

    [Fact]
    public async Task With_stripe_the_card_goes_to_stripe_and_only_a_signed_callback_settles_the_payment()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Listing(next: 1);
        using FakeStripe fake = new();
        await using WebApplicationFactory<Program> stripeHost = api.Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Payments:Stripe:SecretKey", "sk_test_api");
            b.UseSetting("Payments:Stripe:WebhookSecret", WebhookSecret);
            b.ConfigureTestServices(s => s.AddHttpClient(nameof(StripePaymentGateway)).ConfigurePrimaryHttpMessageHandler(() => fake));
        });
        using HttpClient anonymous = stripeHost.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{ApiFixture.Host}/"), AllowAutoRedirect = false });
        using HttpClient admin = await SignedInAsync(AdminOf(org));
        string adult = await api.NewAdultAsync();
        using HttpClient family = await SignedInAsync(adult);

        // The Stripe host signs its own tokens (each test host draws its signing key), so the family signs in there too.
        HttpResponseMessage signIn = await anonymous.PostAsJsonAsync("api/v1/auth/token", new { email = adult, password = await api.SetPasswordAsync(adult) }, Ct);
        using HttpClient familyViaStripe = ApiFixture.WithBearer(
            stripeHost.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{ApiFixture.Host}/"), AllowAutoRedirect = false }),
            (await JsonAsync(signIn)).GetProperty("accessToken").GetString()!);

        JsonElement set = await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/fees",
            new { fees = new[] { new { label = "Term fee", amountCents = 12500, cadence = "seasonal", isDefault = true } } }, Ct));
        Guid fee = set[0].GetProperty("id").GetGuid();
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
        await JsonAsync(await family.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 2 }, Ct));
        Guid application = await AcceptedAsync(family, admin, org, listing, "Esi");

        // Paying makes a Stripe intent: its client secret comes back for Stripe's own card form, and isn't kept.
        string key = Key();
        JsonElement paying = await JsonAsync(await PostWithKeyAsync(familyViaStripe, "api/v1/payments/intents", new { applicationId = application, feeScheduleId = fee }, key),
            HttpStatusCode.Created);
        Guid payment = paying.GetProperty("id").GetGuid();
        (paying.GetProperty("provider").GetString(), paying.GetProperty("status").GetString(), paying.GetProperty("amountCents").GetInt32()).ShouldBe(("stripe", "requires_action", 12500));
        string reference = await api.ScalarAsync<string>("select provider_ref from enrollment.payment_intent where id = @p", ("p", payment));
        paying.GetProperty("clientSecret").GetString().ShouldBe(reference + "_secret_abc");
        (HttpRequestMessage made, string form) = fake.Seen.Single();
        made.Headers.GetValues("Idempotency-Key").Single().ShouldEndWith("-" + key, Case.Sensitive, "the family's key, scoped to the tenant and the adult");
        form.ShouldContain("amount=12500");
        form.ShouldContain("currency=usd");

        // Asking again gives the same payment, with its client secret fetched again.
        JsonElement again = await JsonAsync(await PostWithKeyAsync(familyViaStripe, "api/v1/payments/intents", new { applicationId = application, feeScheduleId = fee }, Key()),
            HttpStatusCode.Created);
        (again.GetProperty("id").GetGuid(), again.GetProperty("clientSecret").GetString()).ShouldBe((payment, reference + "_secret_abc"));
        fake.Seen.Count(s => s.Request.Method == HttpMethod.Get).ShouldBe(1);

        // Only a payment provider recorded as Stripe's can be settled by Stripe; the admin can't mark it received.
        await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/payments/{payment}/received", new { reason = "Cash" }, Ct), HttpStatusCode.Conflict, "payments.not_manual");

        // Callbacks: unknown providers, missing, forged and stale signatures are refused and queue nothing.
        string succeeded = StripeEvent("evt_" + Guid.NewGuid().ToString("N"), "payment_intent.succeeded", $$"""{"id":"{{reference}}","object":"payment_intent","status":"succeeded"}""");
        await ProblemAsync(await CallbackAsync(anonymous, "manual", succeeded, Signed(succeeded, DateTimeOffset.UtcNow)), HttpStatusCode.NotFound, "payments.webhook_provider");
        await ProblemAsync(await CallbackAsync(anonymous, "stripe", succeeded, null), HttpStatusCode.BadRequest, "payments.webhook_signature");
        await ProblemAsync(await CallbackAsync(anonymous, "stripe", succeeded.Replace("succeeded", "canceled", StringComparison.Ordinal), Signed(succeeded, DateTimeOffset.UtcNow)),
            HttpStatusCode.BadRequest, "payments.webhook_signature");
        await ProblemAsync(await CallbackAsync(anonymous, "stripe", succeeded, Signed(succeeded, DateTimeOffset.UtcNow.AddMinutes(-10))), HttpStatusCode.BadRequest, "payments.webhook_signature");
        await ProblemAsync(await CallbackAsync(api.Client(), "stripe", succeeded, Signed(succeeded, DateTimeOffset.UtcNow)), HttpStatusCode.NotFound, "payments.webhook_provider");
        (await RunPaymentEventsAsync(reference)).ShouldBe(0);

        // A genuine event about nothing a payment changes is acknowledged and ignored.
        string unrelated = StripeEvent("evt_" + Guid.NewGuid().ToString("N"), "customer.created", """{"id":"cus_1"}""");
        (await CallbackAsync(anonymous, "stripe", unrelated, Signed(unrelated, DateTimeOffset.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The signed success, delivered twice as Stripe may, queues once and settles the payment on the worker.
        (await CallbackAsync(anonymous, "stripe", succeeded, Signed(succeeded, DateTimeOffset.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CallbackAsync(anonymous, "stripe", succeeded, Signed(succeeded, DateTimeOffset.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RunPaymentEventsAsync(reference)).ShouldBe(1);
        (await JsonAsync(await family.GetAsync(new Uri($"api/v1/payments/intents/{payment}", UriKind.Relative), Ct))).GetProperty("status").GetString().ShouldBe("succeeded");

        // A late "processing" changes nothing; a full refund does.
        string late = StripeEvent("evt_" + Guid.NewGuid().ToString("N"), "payment_intent.processing", $$"""{"id":"{{reference}}"}""");
        (await CallbackAsync(anonymous, "stripe", late, Signed(late, DateTimeOffset.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RunPaymentEventsAsync(reference)).ShouldBe(1);
        (await api.ScalarAsync<string>("select status from enrollment.payment_intent where id = @p", ("p", payment))).ShouldBe("succeeded");
        string refunded = StripeEvent("evt_" + Guid.NewGuid().ToString("N"), "charge.refunded", $$"""{"id":"ch_1","payment_intent":"{{reference}}","refunded":true}""");
        (await CallbackAsync(anonymous, "stripe", refunded, Signed(refunded, DateTimeOffset.UtcNow))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RunPaymentEventsAsync(reference)).ShouldBe(1);
        (await api.ScalarAsync<string>("select status from enrollment.payment_intent where id = @p", ("p", payment))).ShouldBe("refunded");

        // When Stripe can't be reached the family is told to try later, and nothing is kept.
        Guid second = await AcceptedAsync(family, admin, org, listing, "Yaw");
        fake.Down = true;
        await ProblemAsync(await PostWithKeyAsync(familyViaStripe, "api/v1/payments/intents", new { applicationId = second, feeScheduleId = fee }, Key()),
            HttpStatusCode.ServiceUnavailable, "payments.provider");
        (await api.ScalarAsync<long>("select count(*) from enrollment.payment_intent where application_id = @a", ("a", second))).ShouldBe(0);
    }
}
