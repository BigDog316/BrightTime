using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BrightTime.Abstractions.Messaging;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;

namespace BrightTime.Api.Tests;

/// <summary>
/// Email plus (D31, Q3's default): making a child profile sends the guardian a consent email
/// with the account disclosure and a single-use link; answering it, signed in, verifies the link
/// and records the consent; a confirmatory email follows.
/// </summary>
/// <remarks>Each test signs in as its own seeded adult (admins from index 30 on), so tests never share a guardian or an inbox.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class EmailPlusTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private string Admin(int index) => $"admin@{api.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).ElementAt(index).Slug}.example.org";

    private async Task<HttpClient> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        return ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
    }

    private static string RecentBirthMonth => DateTime.UtcNow.AddYears(-7).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Makes a child and returns its id with the consent email the guardian got.</summary>
    private async Task<(Guid Child, EmailMessage Mail)> CreateChildAsync(HttpClient guardian, string email, string name = "Noor")
    {
        int seen = api.Mail.To(email).Count;
        HttpResponseMessage made = await guardian.PostAsJsonAsync("api/v1/children", new { givenName = name, birthMonth = RecentBirthMonth, relationship = "parent" }, Ct);
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid child = (await made.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        return (child, await api.Mail.WaitForAsync(email, seen));
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, Guid link, string token) =>
        client.PostAsJsonAsync($"api/v1/guardian-links/{link}/verify", new { token }, Ct);

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("type").GetString();

    private async Task<long> AuditedAsync(string action, Guid child)
    {
        // Audited by the dispatcher after the send, so give it a moment.
        for (int i = 0; i < 100; i++)
        {
            long rows = await api.ScalarAsync<long>(
                "select count(*) from platform.audit_event where action = @a and subject_ids = array[@c] and detail->>'email_sent' = 'true'", ("a", action), ("c", child));
            if (rows > 0)
            {
                return rows;
            }

            await Task.Delay(50, Ct);
        }

        return 0;
    }

    [Fact]
    public async Task The_guardian_consents_by_email_and_is_sent_a_confirmation()
    {
        RequireDatabase();
        string email = Admin(30);
        using HttpClient guardian = await SignedInAsync(email);

        (Guid child, EmailMessage request) = await CreateChildAsync(guardian, email, "Noor");

        // The notice: the account disclosure, word for word, and the child it is about.
        request.TextBody.ShouldContain(ConsentDisclosures.For(ConsentScope.Account).Text);
        request.Subject.ShouldContain("Noor");
        (Guid link, string token) = CapturedMail.ConsentLink(request);

        // Only the token's hash is stored.
        byte[] stored = await api.ScalarAsync<byte[]>("select email_token_hash from identity.guardian_link where id = @l", ("l", link));
        stored.ShouldBe(SHA256.HashData(Convert.FromBase64String(token.Replace('-', '+').Replace('_', '/') + "=")));

        HttpResponseMessage verified = await VerifyAsync(guardian, link, token);

        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await verified.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("active");
        body.GetProperty("link").GetProperty("verificationMethod").GetString().ShouldBe(VerificationMethod.EmailPlus);
        body.GetProperty("capabilities").GetProperty("hasAccountConsent").GetBoolean().ShouldBeTrue();
        (await api.ScalarAsync<string>("select method from identity.parental_consent where child_user_id = @c and scope = 'account' and revoked_at is null", ("c", child)))
            .ShouldBe(VerificationMethod.EmailPlus);
        (await api.ScalarAsync<byte[]>("select consent_text_hash from identity.parental_consent where child_user_id = @c and scope = 'account'", ("c", child)))
            .ShouldBe(ConsentDisclosures.For(ConsentScope.Account).Hash);
        (await api.ScalarAsync<bool>("select email_token_hash is null from identity.guardian_link where id = @l", ("l", link))).ShouldBeTrue();

        // The plus: a confirmation to the same address, which says how to withdraw.
        EmailMessage confirmation = await api.Mail.WaitForAsync(email, alreadySeen: 1);
        confirmation.Subject.ShouldContain("Noor");
        confirmation.TextBody.ShouldContain("withdraw this consent at any time");
        confirmation.TextBody.ShouldContain(ConsentDisclosures.For(ConsentScope.Account).Text);

        (await AuditedAsync(Guardianship.ConsentEmailSent, child)).ShouldBe(1);
        (await AuditedAsync(Guardianship.ConfirmationSent, child)).ShouldBe(1);
        (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and subject_ids = array[@c] and detail->>'method' = 'email_plus'",
            ("a", Guardianship.LinkVerified), ("c", child))).ShouldBe(1);
    }

    [Fact]
    public async Task A_consent_link_works_once()
    {
        RequireDatabase();
        string email = Admin(31);
        using HttpClient guardian = await SignedInAsync(email);
        (Guid child, EmailMessage request) = await CreateChildAsync(guardian, email);
        (Guid link, string token) = CapturedMail.ConsentLink(request);
        (await VerifyAsync(guardian, link, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await api.Mail.WaitForAsync(email, alreadySeen: 1);

        HttpResponseMessage again = await VerifyAsync(guardian, link, token);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(again)).ShouldBe("urn:brighttime:problem:consent.already_verified");
        (await api.ScalarAsync<long>("select count(*) from identity.parental_consent where child_user_id = @c", ("c", child))).ShouldBe(1);
        (await guardian.PostAsync(new Uri($"api/v1/guardian-links/{link}/consent-email", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        api.Mail.To(email).Count.ShouldBe(2, "a refused second use sends no second confirmation");
    }

    [Fact]
    public async Task An_expired_link_is_refused_and_a_new_email_replaces_it()
    {
        RequireDatabase();
        string email = Admin(32);
        using HttpClient guardian = await SignedInAsync(email);
        (Guid child, EmailMessage first) = await CreateChildAsync(guardian, email);
        (Guid link, string expired) = CapturedMail.ConsentLink(first);
        await api.ExecuteAsync(
            "update identity.guardian_link set email_token_issued_at = now() - interval '4 days', email_token_expires_at = now() - interval '1 day' where id = @l", ("l", link));

        HttpResponseMessage late = await VerifyAsync(guardian, link, expired);

        late.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        (await ProblemTypeAsync(late)).ShouldBe("urn:brighttime:problem:consent.token_expired");
        (await api.ScalarAsync<string>("select status from identity.app_user where id = @c", ("c", child))).ShouldBe("pending_consent");

        // A new email; the old token stops working, and the new one is not sent twice in a minute.
        (await guardian.PostAsync(new Uri($"api/v1/guardian-links/{link}/consent-email", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (Guid sameLink, string fresh) = CapturedMail.ConsentLink(await api.Mail.WaitForAsync(email, alreadySeen: 1));
        sameLink.ShouldBe(link);
        fresh.ShouldNotBe(expired);
        HttpResponseMessage soon = await guardian.PostAsync(new Uri($"api/v1/guardian-links/{link}/consent-email", UriKind.Relative), null, Ct);
        soon.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ProblemTypeAsync(soon)).ShouldBe("urn:brighttime:problem:consent.email_recently_sent");

        HttpResponseMessage old = await VerifyAsync(guardian, link, expired);
        old.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemTypeAsync(old)).ShouldBe("urn:brighttime:problem:consent.token_invalid");

        (await VerifyAsync(guardian, link, fresh)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_the_guardian_the_email_was_for_can_use_it()
    {
        RequireDatabase();
        string email = Admin(33);
        using HttpClient guardian = await SignedInAsync(email);
        (Guid child, EmailMessage request) = await CreateChildAsync(guardian, email);
        (Guid link, string token) = CapturedMail.ConsentLink(request);

        // Someone else signed in, holding the forwarded email: as if the link did not exist.
        using HttpClient stranger = await SignedInAsync(Admin(34));
        HttpResponseMessage theirs = await VerifyAsync(stranger, link, token);
        theirs.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ProblemTypeAsync(theirs)).ShouldBe("urn:brighttime:problem:consent.link_not_found");
        (await stranger.PostAsync(new Uri($"api/v1/guardian-links/{link}/consent-email", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Nobody signed in at all.
        using HttpClient anonymous = api.Client();
        (await VerifyAsync(anonymous, link, token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The guardian, with a token that is not theirs, or not a token.
        HttpResponseMessage wrong = await VerifyAsync(guardian, link, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemTypeAsync(wrong)).ShouldBe("urn:brighttime:problem:consent.token_invalid");
        (await VerifyAsync(guardian, link, "not a token")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await api.ScalarAsync<string>("select status from identity.guardian_link where id = @l", ("l", link))).ShouldBe("pending");
        (await api.ScalarAsync<long>("select count(*) from identity.parental_consent where child_user_id = @c", ("c", child))).ShouldBe(0);

        // Refusals spend nothing: the real token still works for the real guardian.
        (await VerifyAsync(guardian, link, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_form_spends_the_email_token()
    {
        RequireDatabase();
        string email = Admin(35);
        using HttpClient guardian = await SignedInAsync(email);
        (_, EmailMessage request) = await CreateChildAsync(guardian, email);
        (Guid link, string token) = CapturedMail.ConsentLink(request);

        using HttpClient organiser = await SignedInAsync("organiser@kettle.example.org");
        (await organiser.PostAsync(new Uri($"api/v1/organiser/guardian-links/{link}/attest", UriKind.Relative), null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await api.ScalarAsync<bool>("select email_token_hash is null from identity.guardian_link where id = @l", ("l", link))).ShouldBeTrue();
        HttpResponseMessage after = await VerifyAsync(guardian, link, token);
        after.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(after)).ShouldBe("urn:brighttime:problem:consent.already_verified");
    }

    [Fact]
    public async Task The_consent_email_is_in_the_guardians_language_with_the_disclosure_marked_as_english()
    {
        RequireDatabase();
        string email = Admin(36);
        await api.ExecuteAsync("update identity.app_user set locale = 'es' where normalized_email = upper(@e)", ("e", email));
        using HttpClient guardian = await SignedInAsync(email);

        (_, EmailMessage request) = await CreateChildAsync(guardian, email, "Luz");

        request.Locale.Code.ShouldBe("es");
        request.Subject.ShouldStartWith("Da tu consentimiento");
        request.HtmlBody.ShouldContain("<p lang=\"en\" dir=\"ltr\">");
        request.TextBody.ShouldContain("Por ahora este texto está en inglés");
    }
}
