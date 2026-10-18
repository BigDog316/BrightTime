using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BrightTime.Api.Tests;

[Collection(ApiDatabase.Name)]
public sealed class HealthTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task Liveness_answers_on_any_address_and_runs_no_checks()
    {
        RequireDatabase();
        using HttpClient probe = api.Client("10.0.0.7");

        HttpResponseMessage response = await probe.GetAsync(new Uri("health/live", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("Healthy");
        body.GetProperty("checks").EnumerateObject().ShouldBeEmpty();
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Readiness_checks_the_database_and_reports_the_queues_without_a_tenant()
    {
        RequireDatabase();
        using HttpClient probe = api.Client("10.0.0.7");

        HttpResponseMessage response = await probe.GetAsync(new Uri("health/ready", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement checks = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("checks");
        checks.GetProperty("database").GetProperty("status").GetString().ShouldBe("Healthy");
        JsonElement queues = checks.GetProperty("queues");
        queues.GetProperty("status").GetString().ShouldBeOneOf("Healthy", "Degraded");
        queues.GetProperty("data").GetProperty("outboxPending").GetInt64().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Stuck_queues_degrade_readiness_without_taking_the_host_out()
    {
        RequireDatabase();
        await api.ExecuteAsync(
            """
            insert into platform.outbox_message (message_id, tenant_id, occurred_at, source_module, message_type, payload)
            values (gen_random_uuid(), @t, now() - interval '10 minutes', 'test', 'test.health.stuck', '{}')
            """, ("t", BrightTime.Seed.Kettle.KettleSeed.TenantId));
        try
        {
            using HttpClient probe = api.Client("10.0.0.7");
            HttpResponseMessage response = await probe.GetAsync(new Uri("health/ready", UriKind.Relative), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, "degraded is still ready");
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            body.GetProperty("status").GetString().ShouldBe("Degraded");
            body.GetProperty("checks").GetProperty("queues").GetProperty("description").GetString()!.ShouldContain("outbox");
        }
        finally
        {
            await api.ExecuteAsync("delete from platform.outbox_message where message_type = 'test.health.stuck'");
        }
    }
}
