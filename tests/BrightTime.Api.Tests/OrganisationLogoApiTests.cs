using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Tests;
using BrightTime.Ui.Shared;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Api.Tests;

/// <summary>
/// An organisation's logo in the public API (D109): on its profile, and with the organisation on
/// each of its listings and results, as a public media id. Read through the app's own client too.
/// </summary>
/// <remarks>Gives a seeded organisation a logo straight in the tables, and takes it away again.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class OrganisationLogoApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    [Fact]
    public async Task An_organisations_logo_is_on_its_profile_and_with_it_on_its_listings()
    {
        RequireDatabase();
        SeedListing listing = api.Data.Listings.Where(l => l.Status == "published")
            .GroupBy(l => l.Title, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())
            .OrderBy(l => l.Slug, StringComparer.Ordinal).Skip(5).First();
        SeedOrganisation org = api.Data.Organisations.Single(o => o.Id == listing.OrgId);
        Guid logo = UuidV7.NewId();
        byte[] bytes = TestImages.Png(400, 400);
        await api.ExecuteAsync(
            """
            insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, uploaded_by, visibility)
            values (@m, @t, @k, 'image/png', @n, @h, 400, 400, 'clean', (select id from identity.app_user order by id limit 1), 'public');
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) values (@t, @k, 'image/png', @b, @h);
            update directory.organisation set logo_media_id = @m where id = @o;
            """,
            ("m", logo), ("t", KettleSeed.TenantId), ("k", "images/" + logo.ToString("N") + ".png"), ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)),
            ("b", bytes), ("o", org.Id));
        try
        {
            using HttpClient anyone = api.Client();
            JsonElement profile = await anyone.GetFromJsonAsync<JsonElement>($"api/v1/public/organisations/{org.Slug}", Ct);
            profile.GetProperty("logo").GetGuid().ShouldBe(logo);
            JsonElement detail = await anyone.GetFromJsonAsync<JsonElement>($"api/v1/public/listings/{listing.Slug}", Ct);
            detail.GetProperty("organisation").GetProperty("logo").GetGuid().ShouldBe(logo);

            await using ServiceProvider app = new ServiceCollection()
                .AddLogging()
                .AddBrightTimeApp(new AppEndpoints(new Uri("https://kettle.localhost/api/v1/"), new Uri("https://kettle.localhost/")), _ => api.AppClient())
                .BuildServiceProvider();
            await app.GetRequiredService<AppState>().StartAsync(Ct);
            BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();
            (await client.ListingAsync(listing.Slug, Ct)).Value!.Organisation.Logo.ShouldBe(logo);
            Page<ListingSummary> found = (await client.SearchAsync(new SearchFilters { Text = listing.Title }, ct: Ct)).Value!;
            found.Items.Single(i => i.Id == listing.Id).Organisation!.Logo.ShouldBe(logo);
            found.Items.Where(i => i.Organisation?.Id != org.Id).ShouldAllBe(i => i.Organisation == null || i.Organisation.Logo == null, "no other seeded organisation has a logo");
        }
        finally
        {
            await api.ExecuteAsync(
                """
                update directory.organisation set logo_media_id = null where id = @o;
                delete from platform.media_blob where tenant_id = @t and object_key = @k;
                delete from platform.media_object where id = @m;
                """,
                ("o", org.Id), ("t", KettleSeed.TenantId), ("k", "images/" + logo.ToString("N") + ".png"), ("m", logo));
        }
    }
}
