using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrightTime.Abstractions.Messaging;

namespace BrightTime.Api.Tests;

/// <summary>
/// Youth accounts (D35): a young person of 13 to 17 registering from the app, and a child
/// profile handed over to its owner at 13 (the claim).
/// </summary>
/// <remarks>Admins 50 to 53, one per test, as guardians and as an address that already has an account.</remarks>
[Collection(ApiDatabase.Name)]
public sealed partial class YouthAccountTests(ApiFixture api)
{
    private const string StrongPassword = "lantern orchard pebble 47";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private static string Fresh(string who) => $"{who}-{Guid.NewGuid():N}@example.net";

    private static string MonthsAgo(int years) =>
        DateTime.UtcNow.AddYears(-years).AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    private async Task<long> JobsAsync(Guid user, string reason) =>
        await api.ScalarAsync<long>("select count(*) from platform.job where unique_key = @k",
            ("k", $"identity.recompute_capabilities:{user:N}:{reason}"));

    /// <summary>Polls briefly: the account is made after the response.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        T value = await read();
        for (int i = 0; i < 100 && !done(value); i++)
        {
            await Task.Delay(50, Ct);
            value = await read();
        }

        return value;
    }

    [Fact]
    public async Task A_young_person_registers_by_birth_month_and_under_13_is_turned_away_with_nothing_kept()
    {
        RequireDatabase();
        using HttpClient app = api.Client();

        // 15: a youth account, its birth month kept, its capabilities booked for the worker.
        string teen = Fresh("teen");
        (await app.PostAsJsonAsync("api/v1/auth/register", new { email = teen, birthMonth = MonthsAgo(15) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        EmailMessage welcome = await api.Mail.WaitForAsync(teen);
        welcome.TextBody.ShouldContain("/account/reset?token=");
        Guid id = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e) and account_kind = 'youth' and age_band = '15_17' and birth_month is not null", ("e", teen));
        (await EventuallyAsync(() => JobsAsync(id, "registered"), n => n > 0)).ShouldBe(1);

        // 11: refused at once, whatever the address, and nothing made or sent.
        string child = Fresh("child");
        HttpResponseMessage young = await app.PostAsJsonAsync("api/v1/auth/register", new { email = child, birthMonth = MonthsAgo(11) }, Ct);
        young.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemTypeAsync(young)).ShouldBe("urn:brighttime:problem:register.under_13");
        await Task.Delay(300, Ct);
        (await api.ScalarAsync<long>("select count(*) from identity.app_user where normalized_email = upper(@e)", ("e", child))).ShouldBe(0);
        api.Mail.To(child).ShouldBeEmpty();

        // 30: an adult account, which keeps no birth month.
        string adult = Fresh("adult");
        (await app.PostAsJsonAsync("api/v1/auth/register", new { email = adult, birthMonth = MonthsAgo(30) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await api.Mail.WaitForAsync(adult);
        (await api.ScalarAsync<long>("select count(*) from identity.app_user where normalized_email = upper(@e) and account_kind = 'adult' and birth_month is null", ("e", adult))).ShouldBe(1);

        // A month that is no month, or one to come.
        HttpResponseMessage future = await app.PostAsJsonAsync("api/v1/auth/register", new { email = Fresh("x"), birthMonth = DateTime.UtcNow.AddMonths(2).ToString("yyyy-MM", CultureInfo.InvariantCulture) }, Ct);
        future.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_youth_registration_leaves_an_existing_account_as_it_was()
    {
        RequireDatabase();
        using HttpClient app = api.Client();
        string existing = Admin(50);
        int seen = api.Mail.To(existing).Count;

        (await app.PostAsJsonAsync("api/v1/auth/register", new { email = existing, birthMonth = MonthsAgo(14) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await api.Mail.WaitForAsync(existing, seen)).TextBody.ShouldContain("There is already one for that address.");
        (await api.ScalarAsync<string>("select account_kind from identity.app_user where normalized_email = upper(@e)", ("e", existing))).ShouldBe("adult");
    }

    [Fact]
    public async Task A_guardian_hands_a_13_year_old_their_profile_and_the_young_person_claims_it()
    {
        RequireDatabase();
        string guardianEmail = Admin(51);
        using HttpClient guardian = await SignedInAsync(guardianEmail);
        Guid child = await ConsentedChildAsync(guardian, guardianEmail);
        string own = Fresh("kid");

        // Not before 13.
        HttpResponseMessage early = await guardian.PostAsJsonAsync($"api/v1/children/{child}/claim", new { email = own }, Ct);
        early.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemTypeAsync(early)).ShouldBe("urn:brighttime:problem:consent.claim_too_young");

        // Time passes: the child is 13 and a few months.
        await TurnThirteenAsync(child);
        (await guardian.PostAsJsonAsync($"api/v1/children/{child}/claim", new { email = own }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        EmailMessage mail = await api.Mail.WaitForAsync(own);
        string token = ClaimToken(mail);

        // Still the guardian's child profile until the young person answers.
        (await api.ScalarAsync<string>("select account_kind from identity.app_user where id = @c", ("c", child))).ShouldBe("child");

        using HttpClient app = api.Client();
        HttpResponseMessage weak = await app.PostAsJsonAsync("api/v1/auth/claim", new { token, password = "short" }, Ct);
        weak.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemTypeAsync(weak)).ShouldBe("urn:brighttime:problem:password.weak");

        (await app.PostAsJsonAsync("api/v1/auth/claim", new { token, password = StrongPassword }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await api.ScalarAsync<long>(
            "select count(*) from identity.app_user where id = @c and account_kind = 'youth' and normalized_email = upper(@e) and email_confirmed and password_hash is not null",
            ("c", child), ("e", own))).ShouldBe(1);
        (await JobsAsync(child, "claimed")).ShouldBe(1);
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'consent.account_claimed' and subject_ids = array[@c]", ("c", child))).ShouldBe(1);

        // The young person signs in as themselves; the guardian still sees them, now with their own sign-in.
        TokenPair tokens = await api.SignInAsync(own, StrongPassword);
        tokens.AccessToken.ShouldNotBeNullOrEmpty();
        JsonElement view = await guardian.GetFromJsonAsync<JsonElement>($"api/v1/children/{child}", Ct);
        view.GetProperty("accountKind").GetString().ShouldBe("youth");

        // No longer under 13: the scopes closed to children are the guardian's to consider (here a
        // signed form is still needed, since email plus verified the link).
        JsonElement showcase = (await guardian.GetFromJsonAsync<JsonElement>("api/v1/consent/disclosures", Ct)).EnumerateArray()
            .Single(d => d.GetProperty("scope").GetString() == "showcase_publication");
        HttpResponseMessage publish = await guardian.PostAsJsonAsync($"api/v1/children/{child}/consents",
            new { scope = "showcase_publication", disclosureHash = showcase.GetProperty("hash").GetString() }, Ct);
        (await ProblemTypeAsync(publish)).ShouldBe("urn:brighttime:problem:consent.method_insufficient");

        // The link works once, and the profile can be handed over once.
        HttpResponseMessage again = await app.PostAsJsonAsync("api/v1/auth/claim", new { token, password = StrongPassword }, Ct);
        (await ProblemTypeAsync(again)).ShouldBe("urn:brighttime:problem:account_claim.invalid");
        HttpResponseMessage twice = await guardian.PostAsJsonAsync($"api/v1/children/{child}/claim", new { email = Fresh("kid") }, Ct);
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(twice)).ShouldBe("urn:brighttime:problem:consent.already_claimed");
    }

    [Fact]
    public async Task A_claim_to_an_address_with_an_account_changes_nothing_and_tells_only_its_owner()
    {
        RequireDatabase();
        string guardianEmail = Admin(52);
        using HttpClient guardian = await SignedInAsync(guardianEmail);
        Guid child = await ConsentedChildAsync(guardian, guardianEmail);
        await TurnThirteenAsync(child);

        string taken = Admin(53);
        int seen = api.Mail.To(taken).Count;
        (await guardian.PostAsJsonAsync($"api/v1/children/{child}/claim", new { email = taken }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        EmailMessage told = await api.Mail.WaitForAsync(taken, seen);
        told.Subject.ShouldBe("Someone tried to use your email address");
        told.TextBody.ShouldNotContain("/account/claim");
        (await api.ScalarAsync<string>("select account_kind from identity.app_user where id = @c", ("c", child))).ShouldBe("child");

        // Someone else's child: no.
        using HttpClient stranger = await SignedInAsync(Admin(50));
        HttpResponseMessage notYours = await stranger.PostAsJsonAsync($"api/v1/children/{child}/claim", new { email = Fresh("kid") }, Ct);
        notYours.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemTypeAsync(notYours)).ShouldBe("urn:brighttime:problem:consent.not_guardian");
    }

    /// <summary>A seven-year-old's profile, with the guardian's consent given by email plus.</summary>
    private async Task<Guid> ConsentedChildAsync(HttpClient guardian, string guardianEmail)
    {
        int seen = api.Mail.To(guardianEmail).Count;
        HttpResponseMessage made = await guardian.PostAsJsonAsync("api/v1/children",
            new { givenName = "Ada", birthMonth = MonthsAgo(7), relationship = "parent" }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid child = (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        (Guid link, string token) = CapturedMail.ConsentLink(await api.Mail.WaitForAsync(guardianEmail, seen));
        (await guardian.PostAsJsonAsync($"api/v1/guardian-links/{link}/verify", new { token }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return child;
    }

    private Task TurnThirteenAsync(Guid child) =>
        api.ExecuteAsync("update identity.app_user set birth_month = date_trunc('month', now() - interval '13 years 3 months')::date where id = @c", ("c", child));

    private static string ClaimToken(EmailMessage message)
    {
        Match m = ClaimLink().Match(message.TextBody);
        m.Success.ShouldBeTrue("The email has no claim link.");
        return Uri.UnescapeDataString(m.Groups[1].Value);
    }

    [GeneratedRegex(@"/account/claim\?token=([A-Za-z0-9_%-]+)")]
    private static partial Regex ClaimLink();
}
