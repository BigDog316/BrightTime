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
using Microsoft.Extensions.Logging.Abstractions;

namespace BrightTime.Api.Tests;

/// <summary>
/// A listing's pictures in the public API (D105): the live ones on its detail, the main one first,
/// and the main one on its search result; none that waits for an organiser. Read through the
/// app's own client too, so its records keep in step.
/// </summary>
/// <remarks>Gives a seeded listing pictures straight in the tables, and takes them away again.</remarks>
[Collection(ApiDatabase.Name)]
public sealed class ListingPictureApiTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(api.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private Task ProjectAsync(Guid listing) => SeedProjection.ProjectAsync(api.Connection, KettleSeed.TenantId, [listing], NullLogger.Instance, Ct);

    /// <summary>A clean, public picture of the listing, in the given state.</summary>
    private async Task<Guid> PictureAsync(Guid listing, string alt, short order, string state)
    {
        Guid media = UuidV7.NewId();
        byte[] bytes = TestImages.Png(800, 600);
        await api.ExecuteAsync(
            """
            insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, uploaded_by, visibility)
            values (@m, @t, @k, 'image/png', @n, @h, 800, 600, 'clean', (select id from identity.app_user order by id limit 1), 'public');
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) values (@t, @k, 'image/png', @b, @h);
            insert into opportunity.listing_media (id, listing_id, media_id, alt_text, sort_order, state) values (@i, @l, @m, @a, @o, @s);
            """,
            ("m", media), ("t", KettleSeed.TenantId), ("k", "images/" + media.ToString("N") + ".png"), ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)),
            ("b", bytes), ("i", UuidV7.NewId()), ("l", listing), ("a", alt), ("o", order), ("s", state));
        return media;
    }

    [Fact]
    public async Task A_listings_live_pictures_are_on_its_detail_and_its_main_one_on_its_result()
    {
        RequireDatabase();
        SeedListing listing = api.Data.Listings.Where(l => l.Status == "published")
            .GroupBy(l => l.Title, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Single())
            .OrderBy(l => l.Slug, StringComparer.Ordinal).Skip(3).First();
        Guid second = await PictureAsync(listing.Id, "Seedlings in trays", 1, "live");
        Guid main = await PictureAsync(listing.Id, "Two children planting seedlings", 0, "live");
        Guid waiting = await PictureAsync(listing.Id, "Not approved yet", 2, "waiting");
        await api.ExecuteAsync("update opportunity.listing set hero_media_id = @m where id = @l", ("m", main), ("l", listing.Id));
        await ProjectAsync(listing.Id);
        try
        {
            using HttpClient anyone = api.Client();
            JsonElement detail = await anyone.GetFromJsonAsync<JsonElement>($"api/v1/public/listings/{listing.Slug}", Ct);
            detail.GetProperty("pictures").EnumerateArray().Select(p => $"{p.GetProperty("mediaId").GetGuid()}:{p.GetProperty("alt").GetString()}")
                .ShouldBe([$"{main}:Two children planting seedlings", $"{second}:Seedlings in trays"]);
            detail.GetProperty("primaryLanguage").GetString().ShouldBe(await api.ScalarAsync<string>("select primary_language from opportunity.listing where id = @l", ("l", listing.Id)));
            detail.GetRawText().ShouldNotContain(waiting.ToString(), Case.Insensitive, "a picture waiting for an organiser isn't public");

            await using ServiceProvider app = new ServiceCollection()
                .AddLogging()
                .AddBrightTimeApp(new AppEndpoints(new Uri("https://kettle.localhost/api/v1/"), new Uri("https://kettle.localhost/")), _ => api.AppClient())
                .BuildServiceProvider();
            await app.GetRequiredService<AppState>().StartAsync(Ct);
            BrightTimeApi client = app.GetRequiredService<BrightTimeApi>();
            ListingDetail shown = (await client.ListingAsync(listing.Slug, Ct)).Value!;
            shown.Pictures!.Select(p => p.MediaId).ShouldBe([main, second]);
            app.GetRequiredService<AppEndpoints>().Media(main).ShouldBe(new Uri($"https://kettle.localhost/media/{main:D}"));
            app.GetRequiredService<AppEndpoints>().Media(main, AppEndpoints.MediaCard).ShouldBe(new Uri($"https://kettle.localhost/media/{main:D}/480"));

            Page<ListingSummary> found = (await client.SearchAsync(new SearchFilters { Text = listing.Title }, ct: Ct)).Value!;
            found.Items.Single(i => i.Id == listing.Id).Picture.ShouldBe(new BrightTime.Ui.Shared.Api.ListingPicture(main, "Two children planting seedlings"));
            found.Items.Where(i => i.Id != listing.Id).ShouldAllBe(i => i.Picture == null, "no other seeded listing has a picture");
        }
        finally
        {
            await api.ExecuteAsync(
                """
                update opportunity.listing set hero_media_id = null where id = @l;
                delete from opportunity.listing_media where listing_id = @l;
                delete from platform.media_blob b using platform.media_object m where m.id = any(@ids) and b.tenant_id = m.tenant_id and b.object_key = m.object_key;
                delete from platform.media_object where id = any(@ids);
                """,
                ("l", listing.Id), ("ids", new[] { main, second, waiting }));
            await ProjectAsync(listing.Id);
        }
    }
}
