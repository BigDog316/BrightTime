using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

/// <summary>
/// The clearance gate on the API (D99): with the county's rules off, a staff member sees the
/// day's meetings; on, without clearances on file, they're refused and the refusal is audited;
/// once their clearances are recorded, they're let back in. The refusal says why (D100): the kinds
/// missing and expired, and only where the clearance is the reason.
/// </summary>
/// <remarks>
/// Reads only, so any staff member serves: the last one. The rules and clearances are written in
/// the database for the test and removed after it.
/// </remarks>
[Collection(ApiDatabase.Name)]
public sealed class ClearanceGateApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_county_that_enforces_clearances_refuses_uncleared_staff_the_days_meetings()
    {
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");
        var staff = api.Data.Users.Where(u => u.Email.StartsWith("staff@", StringComparison.Ordinal)).OrderBy(u => u.Email, StringComparer.Ordinal).Last();
        Guid org = staff.Memberships.Single().OrgId;
        string password = await api.SetPasswordAsync(staff.Email);
        TokenPair tokens = await api.SignInAsync(staff.Email, password);
        using HttpClient client = ApiFixture.WithBearer(api.Client(), tokens.AccessToken);
        Uri meetings = new($"api/v1/org/{org}/attendance/meetings", UriKind.Relative);

        try
        {
            (await client.GetAsync(meetings, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the rules are off");

            await api.ExecuteAsync("insert into platform.feature_flag (tenant_id, key, is_enabled) values (@t, 'clearances.required', true)", ("t", api.Data.TenantId));
            (await RefusalAsync(await client.GetAsync(meetings, Ct))).ShouldBe(("urn:brighttime:problem:clearances.not_current", "background_check,mandated_reporter_training", ""));
            (await api.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'directory.clearance.refused' and actor_id = @u and actor_org_id = @o",
                ("u", staff.Id), ("o", org))).ShouldBeGreaterThan(0);

            // Refused for their role, they're told so: the clearance isn't the reason.
            HttpResponseMessage waive = await client.PostAsJsonAsync(new Uri($"api/v1/org/{org}/payments/{Guid.NewGuid()}/waive", UriKind.Relative), new { reason = "Hardship" }, Ct);
            (await RefusalAsync(waive)).Type.ShouldBe("urn:brighttime:problem:payments.forbidden");

            await api.ExecuteAsync("""
                insert into directory.staff_clearance (id, tenant_id, user_id, org_id, clearance_kind, status, completed_on, expires_on)
                values (gen_random_uuid(), @t, @u, @o, 'background_check', 'cleared', current_date - 400, current_date - 1),
                       (gen_random_uuid(), @t, @u, null, 'mandated_reporter_training', 'cleared', current_date - 30, null)
                """, ("t", api.Data.TenantId), ("u", staff.Id), ("o", org));
            (await RefusalAsync(await client.GetAsync(meetings, Ct))).ShouldBe(("urn:brighttime:problem:clearances.not_current", "", "background_check"));

            await api.ExecuteAsync("update directory.staff_clearance set expires_on = current_date + 300 where user_id = @u and clearance_kind = 'background_check'", ("u", staff.Id));
            (await client.GetAsync(meetings, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "an organisation's check and a county-wide training both count");
        }
        finally
        {
            await api.ExecuteAsync("""
                delete from platform.feature_flag where tenant_id = @t and key like 'clearances.%';
                delete from directory.staff_clearance where user_id = @u;
                """, ("t", api.Data.TenantId), ("u", staff.Id));
        }
    }

    /// <summary>A refusal's type, and the kinds missing and expired, comma-separated.</summary>
    private static async Task<(string Type, string Missing, string Expired)> RefusalAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        JsonElement root = problem.RootElement;
        return (root.GetProperty("type").GetString()!, Kinds("missing"), Kinds("expired"));

        string Kinds(string name) => root.TryGetProperty(name, out JsonElement kinds) ? string.Join(',', kinds.EnumerateArray().Select(k => k.GetString())) : "";
    }
}
