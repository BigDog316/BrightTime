using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Open Badges (M4, D66): a badge an organisation defines and publishes, awarded to a young person
/// enrolled with it, signed as an OB 3.0 credential that verifies against the issuer's DID document,
/// hosted as OB 2.0, held by the young person, and revoked with a reason that verifiers see at once.
/// </summary>
/// <remarks>
/// Uses the first builtin listing of the tenth organisation with no staff (PortfolioApiTests takes
/// the ninth). The young person is a fresh adult account applying for themselves.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class BadgeApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private List<(Guid Listing, Guid Org)> Unstaffed()
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        return [.. api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => (l.Id, l.OrgId))];
    }

    private (Guid Listing, Guid Org) Programme(int nth)
    {
        List<(Guid Listing, Guid Org)> unstaffed = Unstaffed();
        Guid org = unstaffed.Select(l => l.Org).Distinct().Skip(nth).First();
        return unstaffed.First(l => l.Org == org);
    }

    private string AdminOf(Guid org) => $"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org";

    private async Task<(HttpClient Client, Guid User)> SignedInAsync(string email)
    {
        string password = await api.SetPasswordAsync(email);
        TokenPair tokens = await api.SignInAsync(email, password);
        Guid user = await api.ScalarAsync<Guid>("select id from identity.app_user where normalized_email = upper(@e)", ("e", email));
        return (ApiFixture.WithBearer(api.Client(), tokens.AccessToken), user);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("type").GetString().ShouldBe("urn:brighttime:problem:" + code);

    private static Uri Relative(string path) => new(path, UriKind.Relative);

    private static byte[] FromWebSafeBase64(string text)
    {
        string padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    /// <summary>A young person's own enrolment with the organisation, linked to their own account.</summary>
    private async Task<Guid> EnrolledAsync(HttpClient learner, Guid learnerId, HttpClient admin, Guid listing, Guid org)
    {
        await JsonAsync(await learner.PutAsJsonAsync("api/v1/family-profile", new { householdSize = 1 }, Ct));
        string dob = DateTime.UtcNow.AddYears(-19).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Guid member = (await JsonAsync(await learner.PostAsJsonAsync("api/v1/family-profile/members",
            new { role = "youth", firstName = "Imani", lastName = "Okafor", dateOfBirth = dob }, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using HttpRequestMessage apply = new(HttpMethod.Post, Relative("api/v1/applications")) { Content = JsonContent.Create(new { listingId = listing, memberId = member }) };
        apply.Headers.Add("Idempotency-Key", "badge-" + Guid.NewGuid().ToString("N"));
        Guid application = (await JsonAsync(await learner.SendAsync(apply, Ct), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await learner.PostAsJsonAsync($"api/v1/applications/{application}/submit", new { shareFields = new[] { "member_names" } }, Ct));
        await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/listings/{listing}/seats", new { capacity = 20 }, Ct));
        await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/applications/{application}/decide", new { decision = "accept" }, Ct));
        await JsonAsync(await learner.PutAsJsonAsync($"api/v1/family-profile/members/{member}/account", new { userId = learnerId }, Ct));
        return await api.ScalarAsync<Guid>("select id from enrollment.enrollment where application_id = @a and status = 'active'", ("a", application));
    }

    [Fact]
    public async Task A_badge_is_awarded_signed_verified_against_the_issuers_DID_hosted_as_OB2_and_revoked()
    {
        RequireDatabase();
        (Guid listing, Guid org) = Programme(9);
        (_, Guid otherOrg) = Programme(8);
        (HttpClient admin, _) = await SignedInAsync(AdminOf(org));
        (HttpClient outsider, _) = await SignedInAsync(AdminOf(otherOrg));
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anonymous = api.Client();
        using (admin)
        using (outsider)
        using (learner)
        {
            Guid enrolment = await EnrolledAsync(learner, learnerId, admin, listing, org);

            // Defined as a draft; criteria must say what earns it; only an admin of the organisation defines.
            object badge = new { name = "River Surveyor", description = "Mapped a stretch of river and its life.", criteria = "Surveyed the river on three visits and logged what lives there.", expiresAfterMonths = 24 };
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes", new { name = "Short", description = "Too short criteria.", criteria = "Did it." }, Ct),
                HttpStatusCode.BadRequest, "badges.criteria");
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{org}/badge-classes", badge, Ct), HttpStatusCode.Forbidden, "badges.forbidden");
            await ProblemAsync(await learner.PostAsJsonAsync($"api/v1/org/{org}/badge-classes", badge, Ct), HttpStatusCode.Forbidden, "badges.forbidden");
            JsonElement made = await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes", badge, Ct), HttpStatusCode.Created);
            Guid classId = made.GetProperty("id").GetGuid();
            (made.GetProperty("status").GetString(), made.GetProperty("slug").GetString()).ShouldBe(("draft", "river-surveyor"));
            (await anonymous.GetAsync(Relative($"api/v1/public/badges/classes/{classId}"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // A draft isn't awarded; it changes until it's published, then never.
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Conflict, "badges.not_published");
            (await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}",
                new { name = "River Surveyor", description = "Mapped a stretch of the Kettle and the life in it.", criteria = "Surveyed the river on three visits and logged what lives there.", expiresAfterMonths = 24 }, Ct)))
                .GetProperty("description").GetString()!.ShouldContain("Kettle");
            (await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{classId}/publish"), null, Ct))).GetProperty("status").GetString().ShouldBe("published");
            await ProblemAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}", badge, Ct), HttpStatusCode.Conflict, "badges.not_draft");

            // Awarded once, to someone enrolled with the organisation; another organisation can't award it.
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = Guid.NewGuid() }, Ct), HttpStatusCode.BadRequest, "badges.recipient");
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Forbidden, "badges.forbidden");
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{otherOrg}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.NotFound, "badges.not_found");
            JsonElement award = await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards",
                new { enrollmentId = enrolment, narrative = "Found a freshwater mussel bed nobody had logged." }, Ct), HttpStatusCode.Created);
            string slug = award.GetProperty("verifySlug").GetString()!;
            Guid assertion = award.GetProperty("id").GetGuid();
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Conflict, "badges.already_awarded");

            // The organisation sees who holds it, as the family's share names them.
            JsonElement awarded = (await JsonAsync(await admin.GetAsync(Relative($"api/v1/org/{org}/badge-classes/{classId}/awards"), Ct))).EnumerateArray().Single();
            (awarded.GetProperty("id").GetGuid(), awarded.GetProperty("recipientName").GetString()!.Contains("Imani", StringComparison.Ordinal)).ShouldBe((assertion, true));

            // Anyone verifies it: valid, and the JWT's signature checks out against the key in the issuer's DID document.
            HttpResponseMessage verifying = await anonymous.GetAsync(Relative($"api/v1/public/badges/{slug.ToUpperInvariant()}"), Ct);
            JsonElement verified = await JsonAsync(verifying);
            verifying.Headers.GetValues("X-Robots-Tag").Single().ShouldContain("noindex");
            (verified.GetProperty("status").GetString(), verified.GetProperty("name").GetString()).ShouldBe(("valid", "River Surveyor"));
            string jwt = verified.GetProperty("jwt").GetString()!;
            string[] parts = jwt.Split('.');
            parts.Length.ShouldBe(3);
            using JsonDocument header = JsonDocument.Parse(FromWebSafeBase64(parts[0]));
            using JsonDocument payload = JsonDocument.Parse(FromWebSafeBase64(parts[1]));
            header.RootElement.GetProperty("alg").GetString().ShouldBe("RS256");
            string kid = header.RootElement.GetProperty("kid").GetString()!;
            JsonElement credential = payload.RootElement;

            string did = credential.GetProperty("issuer").GetProperty("id").GetString()!;
            did.ShouldBe($"did:web:{ApiFixture.Host}:api:v1:public:issuers:{org:N}");
            (credential.GetProperty("iss").GetString(), credential.GetProperty("jti").GetString()).ShouldBe((did, $"https://{ApiFixture.Host}/api/v1/public/badges/{slug}"));
            kid.ShouldStartWith(did + "#");
            HttpResponseMessage resolving = await anonymous.GetAsync(Relative($"api/v1/public/issuers/{org:N}/did.json"), Ct);
            JsonElement didDocument = await JsonAsync(resolving);
            resolving.Content.Headers.ContentType!.MediaType.ShouldBe("application/did+json");
            didDocument.GetProperty("id").GetString().ShouldBe(did);
            didDocument.GetProperty("assertionMethod").EnumerateArray().Select(m => m.GetString()).ShouldContain(kid);
            JsonElement jwk = didDocument.GetProperty("verificationMethod").EnumerateArray().Single(m => m.GetProperty("id").GetString() == kid).GetProperty("publicKeyJwk");
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportParameters(new RSAParameters { Modulus = FromWebSafeBase64(jwk.GetProperty("n").GetString()!), Exponent = FromWebSafeBase64(jwk.GetProperty("e").GetString()!) });
                rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromWebSafeBase64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();
                rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1] + "x"), FromWebSafeBase64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeFalse();
            }

            // An OB 3.0 OpenBadgeCredential, its recipient only a salted hash of their account's identifier.
            credential.GetProperty("@context").EnumerateArray().Select(c => c.GetString()).ShouldBe(
                ["https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json"]);
            credential.GetProperty("type").EnumerateArray().Select(t => t.GetString()).ShouldBe(["VerifiableCredential", "OpenBadgeCredential"]);
            credential.GetProperty("credentialStatus").GetProperty("type").GetString().ShouldBe("1EdTechRevocationList");
            credential.GetProperty("validUntil").GetDateTimeOffset().ShouldBeGreaterThan(credential.GetProperty("validFrom").GetDateTimeOffset().AddMonths(23));
            JsonElement subject = credential.GetProperty("credentialSubject");
            subject.GetProperty("achievement").GetProperty("id").GetString().ShouldBe($"https://{ApiFixture.Host}/api/v1/public/badges/classes/{classId:N}");
            subject.GetProperty("narrative").GetString()!.ShouldContain("mussel");
            JsonElement identity = subject.GetProperty("identifier").EnumerateArray().Single();
            string salt = identity.GetProperty("salt").GetString()!;
            identity.GetProperty("identityHash").GetString().ShouldBe(
                "sha256$" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"urn:uuid:{learnerId}" + salt))));
            string everything = verified.GetRawText() + Encoding.UTF8.GetString(FromWebSafeBase64(parts[1]));
            everything.ShouldNotContain(learnerId.ToString());
            everything.ShouldNotContain("Okafor");
            everything.ShouldNotContain("Imani");

            // The documents it points at resolve, for anyone.
            (await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/badges/classes/{classId}"), Ct))).GetProperty("type").EnumerateArray().Single().GetString().ShouldBe("Achievement");
            HttpResponseMessage image = await anonymous.GetAsync(Relative($"api/v1/public/badges/classes/{classId}/image.svg"), Ct);
            image.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
            (await image.Content.ReadAsStringAsync(Ct)).ShouldStartWith("<svg");

            // And as OB 2.0, hosted: assertion, badge class and issuer.
            JsonElement assertion2 = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/badges/{slug}/ob2"), Ct));
            (assertion2.GetProperty("@context").GetString(), assertion2.GetProperty("type").GetString()).ShouldBe(("https://w3id.org/openbadges/v2", "Assertion"));
            JsonElement recipient = assertion2.GetProperty("recipient");
            (recipient.GetProperty("hashed").GetBoolean(), recipient.GetProperty("identity").GetString()).ShouldBe((true, identity.GetProperty("identityHash").GetString()));
            string badgeClass2 = assertion2.GetProperty("badge").GetString()!;
            JsonElement class2 = await JsonAsync(await anonymous.GetAsync(new Uri(badgeClass2), Ct));
            (class2.GetProperty("type").GetString(), class2.GetProperty("name").GetString()).ShouldBe(("BadgeClass", "River Surveyor"));
            JsonElement issuer2 = await JsonAsync(await anonymous.GetAsync(new Uri(class2.GetProperty("issuer").GetString()!), Ct));
            (issuer2.GetProperty("type").GetString(), issuer2.GetProperty("name").GetString()).ShouldBe(("Issuer", api.Data.Organisations.Single(o => o.Id == org).DisplayName));

            // The young person holds it, with the credential to take anywhere.
            JsonElement held = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/badges"), Ct))).EnumerateArray().Single();
            (held.GetProperty("name").GetString(), held.GetProperty("revoked").GetBoolean(), held.GetProperty("credential").GetString()).ShouldBe(("River Surveyor", false, jwt));

            // Revoked with a reason, by an admin of the organisation: verifiers see it at once, and OB 2.0 answers 410 Gone.
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-assertions/{assertion}/revoke", new { reason = "" }, Ct), HttpStatusCode.BadRequest, "badges.reason");
            await ProblemAsync(await outsider.PostAsJsonAsync($"api/v1/org/{otherOrg}/badge-assertions/{assertion}/revoke", new { reason = "Not ours." }, Ct),
                HttpStatusCode.NotFound, "badges.award_not_found");
            (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-assertions/{assertion}/revoke", new { reason = "Awarded to the wrong young person." }, Ct)))
                .GetProperty("revocationReason").GetString().ShouldBe("Awarded to the wrong young person.");
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-assertions/{assertion}/revoke", new { reason = "Again." }, Ct), HttpStatusCode.Conflict, "badges.already_revoked");
            (await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/badges/{slug}"), Ct))).GetProperty("status").GetString().ShouldBe("revoked");
            JsonElement gone = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/badges/{slug}/ob2"), Ct), HttpStatusCode.Gone);
            (gone.GetProperty("revoked").GetBoolean(), gone.GetProperty("revocationReason").GetString()).ShouldBe((true, "Awarded to the wrong young person."));
            JsonElement revocations = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/issuers/{org:N}/revocations"), Ct));
            revocations.GetProperty("revokedCredentials").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(credential.GetProperty("id").GetString());
            (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/badges"), Ct))).EnumerateArray().Single().GetProperty("revoked").GetBoolean().ShouldBeTrue();

            // Revoked, it can be awarded again; retired, it can't.
            await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Created);
            (await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{classId}/retire"), null, Ct))).GetProperty("status").GetString().ShouldBe("retired");
            await ProblemAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes/{classId}/awards", new { enrollmentId = enrolment }, Ct), HttpStatusCode.Conflict, "badges.not_published");

            // Every signing used the one key the vault holds for the organisation.
            (await api.ScalarAsync<long>("select count(*) from learning.issuer_key k join learning.issuer_profile i on i.id = k.issuer_id where i.org_id = @o", ("o", org))).ShouldBe(1);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.badge.issued' and @a = any(subject_ids)", ("a", assertion))).ShouldBe(1);
        }
    }
}
