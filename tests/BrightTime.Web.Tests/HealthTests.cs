using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrightTime.Web.Tests;

/// <summary>The web host's probes: answered on any address, over plain HTTP, never re-run as a page.</summary>
[Collection(ConsoleDatabase.Name)]
public sealed class HealthTests(ConsoleFixture db)
{
    [Theory]
    [InlineData("health/live")]
    [InlineData("health/ready")]
    public async Task A_probe_to_the_container_gets_a_json_answer(string path)
    {
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");
        using HttpClient probe = db.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://10.0.0.7/"), AllowAutoRedirect = false });

        HttpResponseMessage response = await probe.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("status").GetString().ShouldBeOneOf("Healthy", "Degraded");
    }
}
