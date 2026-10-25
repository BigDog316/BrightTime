using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// Portfolio exports (M4, D68): the printable PDF, the badges as OB 3.0, and a CLR 2.0 signed by
/// the county's site. A young person with a display name, one shown and one hidden record, and a
/// badge earned from a playlist exports all three; the CLR verifies against the site's DID document,
/// nests the badge's own JWT, and leaves out the hidden record and the young person's account.
/// </summary>
/// <remarks>Uses the twelfth organisation with no staff (PlaylistApiTests takes the eleventh).</remarks>
[Collection(ApiDatabase.Name)]
public sealed class PortfolioExportApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private Guid Organisation(int nth)
    {
        HashSet<Guid> staffed = [.. api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).Select(u => u.Memberships.Single().OrgId)];
        return api.Data.Listings
            .Where(l => l.Status == "published" && l.RegistrationMode == "builtin" && !staffed.Contains(l.OrgId)
                        && api.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved")
            .Select(l => l.OrgId).Distinct().Skip(nth).First();
    }

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

    private static Task<HttpResponseMessage> ExportAsync(HttpClient client, string format) =>
        client.PostAsJsonAsync("api/v1/me/portfolio/export", new { format }, Ct);

    [Fact]
    public async Task A_portfolio_exports_as_a_PDF_as_OB3_badges_and_as_a_CLR_the_site_signs()
    {
        RequireDatabase();
        Guid org = Organisation(11);
        (HttpClient admin, _) = await SignedInAsync($"admin@{api.Data.Organisations.Single(o => o.Id == org).Slug}.example.org");
        (HttpClient learner, Guid learnerId) = await SignedInAsync(await api.NewAdultAsync());
        using HttpClient anonymous = api.Client();
        using (admin)
        using (learner)
        {
            // Their own words, one record shown and one hidden.
            await JsonAsync(await learner.PutAsJsonAsync("api/v1/me/portfolio", new { displayName = "Ayo Builds", headline = "Rivers and robots", visibility = "private" }, Ct));
            await api.ExecuteAsync("""
                insert into learning.participation_record (id, tenant_id, user_id, title_snapshot, org_name_snapshot, started_on, ended_on, sessions_attended, hours, source, is_hidden, created_at)
                values (gen_random_uuid(), @t, @u, 'River Rangers', 'Kettle Nature Trust', current_date - 60, current_date - 10, 8, 12.5, 'attendance', false, now()),
                       (gen_random_uuid(), @t, @u, 'Secret Chess Club', 'Kettle Library', current_date - 90, current_date - 70, 3, 4, 'attendance', true, now())
                """, ("t", api.Data.TenantId), ("u", learnerId));

            // Not a format, and no learner record without a badge; the badge export is just empty.
            await ProblemAsync(await ExportAsync(learner, "docx"), HttpStatusCode.BadRequest, "portfolio.export_format");
            await ProblemAsync(await ExportAsync(learner, "clr2"), HttpStatusCode.Conflict, "portfolio.nothing_to_export");
            (await JsonAsync(await ExportAsync(learner, "json_ob3"))).GetProperty("credentials").GetArrayLength().ShouldBe(0);

            // A badge, earned from a one-step playlist.
            Guid badge = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/badge-classes",
                new { name = "River Ranger", description = "Looked after the river.", criteria = "Completed the River Ranger playlist with a reflection." }, Ct),
                HttpStatusCode.Created)).GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/badge-classes/{badge}/publish"), null, Ct));
            Guid playlist = (await JsonAsync(await admin.PostAsJsonAsync($"api/v1/org/{org}/playlists", new { title = "River Ranger", completionBadgeClassId = badge }, Ct),
                HttpStatusCode.Created)).GetProperty("id").GetGuid();
            Guid step = (await JsonAsync(await admin.PutAsJsonAsync($"api/v1/org/{org}/playlists/{playlist}/steps",
                new { steps = new[] { new { kind = "reflection", title = "What did you learn?" } } }, Ct))).GetProperty("steps")[0].GetProperty("id").GetGuid();
            await JsonAsync(await admin.PostAsync(Relative($"api/v1/org/{org}/playlists/{playlist}/publish"), null, Ct));
            await JsonAsync(await learner.PostAsync(Relative($"api/v1/playlists/{playlist}/enroll"), null, Ct));
            await JsonAsync(await learner.PutAsJsonAsync($"api/v1/me/playlists/{playlist}/steps/{step}", new { reflection = "Litter travels a long way downstream." }, Ct));
            string badgeJwt = (await JsonAsync(await learner.GetAsync(Relative("api/v1/me/badges"), Ct))).EnumerateArray().Single().GetProperty("credential").GetString()!;

            // The printable portfolio.
            HttpResponseMessage pdf = await ExportAsync(learner, "pdf");
            pdf.StatusCode.ShouldBe(HttpStatusCode.OK);
            pdf.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
            pdf.Content.Headers.ContentDisposition!.FileNameStar!.ShouldStartWith("portfolio-");
            Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync(Ct))[..5]).ShouldBe("%PDF-");

            // The badges as OB 3.0: each with its signed JWT.
            JsonElement credential = (await JsonAsync(await ExportAsync(learner, "json_ob3"))).GetProperty("credentials").EnumerateArray().Single();
            (credential.GetProperty("name").GetString(), credential.GetProperty("jwt").GetString()).ShouldBe(("River Ranger", badgeJwt));

            // The CLR 2.0: signed by the site under its own DID, verifiable from the DID document.
            HttpResponseMessage clr = await ExportAsync(learner, "clr2");
            clr.StatusCode.ShouldBe(HttpStatusCode.OK, await clr.Content.ReadAsStringAsync(Ct));
            string jwt = await clr.Content.ReadAsStringAsync(Ct);
            string[] parts = jwt.Split('.');
            using JsonDocument header = JsonDocument.Parse(FromWebSafeBase64(parts[0]));
            using JsonDocument payload = JsonDocument.Parse(FromWebSafeBase64(parts[1]));
            JsonElement record = payload.RootElement;
            string did = $"did:web:{ApiFixture.Host}:api:v1:public:issuers:{api.Data.TenantId:N}";
            (record.GetProperty("iss").GetString(), header.RootElement.GetProperty("alg").GetString()).ShouldBe((did, "RS256"));
            record.GetProperty("type").EnumerateArray().Select(t => t.GetString()).ShouldBe(["VerifiableCredential", "ClrCredential"]);
            record.GetProperty("@context").EnumerateArray().Select(c => c.GetString()).ShouldBe(
                ["https://www.w3.org/ns/credentials/v2", "https://purl.imsglobal.org/spec/clr/v2p0/context-2.0.1.json", "https://purl.imsglobal.org/spec/ob/v3p0/context-3.0.3.json"]);
            (record.GetProperty("name").GetString(), record.GetProperty("partial").GetBoolean()).ShouldBe(("Ayo Builds: learner record", true));
            JsonElement subject = record.GetProperty("credentialSubject");
            subject.GetProperty("verifiableCredential").EnumerateArray().Single().GetString().ShouldBe(badgeJwt);
            JsonElement achievement = subject.GetProperty("achievement").EnumerateArray().Single();
            (achievement.GetProperty("name").GetString(), achievement.GetProperty("achievementType").GetString()).ShouldBe(("River Rangers", "CoCurricular"));
            record.GetRawText().ShouldNotContain("Chess");
            record.GetRawText().ShouldNotContain(learnerId.ToString());

            JsonElement didDocument = await JsonAsync(await anonymous.GetAsync(Relative($"api/v1/public/issuers/{api.Data.TenantId:N}/did.json"), Ct));
            string kid = header.RootElement.GetProperty("kid").GetString()!;
            JsonElement jwk = didDocument.GetProperty("verificationMethod").EnumerateArray().Single(m => m.GetProperty("id").GetString() == kid).GetProperty("publicKeyJwk");
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = FromWebSafeBase64(jwk.GetProperty("n").GetString()!), Exponent = FromWebSafeBase64(jwk.GetProperty("e").GetString()!) });
            rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromWebSafeBase64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).ShouldBeTrue();

            // Each export is recorded and audited; no file is kept.
            (await api.ScalarAsync<long>("select count(*) from learning.portfolio_export where user_id = @u and media_id is null", ("u", learnerId))).ShouldBe(4);
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'learning.portfolio.exported' and @u = any(subject_ids)", ("u", learnerId))).ShouldBe(4);
        }
    }
}
