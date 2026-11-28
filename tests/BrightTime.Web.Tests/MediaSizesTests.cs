using System.Net;
using System.Security.Cryptography;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Media;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrightTime.Web.Tests;

/// <summary>
/// Smaller copies of public pictures (D106): made as a picture is added, at each size it's wider
/// than, and served at <c>/media/{id}/{size}</c> as the original is; where there's none, a
/// redirect to the original, kept a year only when there never will be. A private picture gets
/// none, a file that doesn't decode isn't kept, removing a picture takes its copies' bytes too,
/// and the jobs worker sizes the public pictures added before.
/// </summary>
/// <remarks>Adds its own pictures through the library and by SQL, and removes them.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class MediaSizesTests(ConsoleFixture db)
{
    private static readonly ImageRules Rules = new(10 * 1024 * 1024, 1, 1, 6000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private AsyncServiceScope Scope()
    {
        AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        return scope;
    }

    private Task<Guid> SomeoneAsync() => db.ScalarAsync<Guid>("select id from identity.app_user order by id limit 1");

    private async Task<StoredImage?> AddAsync(byte[] file, string visibility)
    {
        UserId uploader = new(await SomeoneAsync());
        await using AsyncServiceScope scope = Scope();
        Result<StoredImage> added = await scope.ServiceProvider.GetRequiredService<IMediaLibrary>().AddImageAsync(file, Rules, visibility, uploader, Ct);
        return added.IsSuccess ? added.Value : null;
    }

    private async Task RemoveAsync(params Guid[] media)
    {
        UserId actor = new(await SomeoneAsync());
        await using AsyncServiceScope scope = Scope();
        foreach (Guid id in media)
        {
            await scope.ServiceProvider.GetRequiredService<IMediaLibrary>().RemoveAsync(id, actor, Ct);
        }
    }

    private Task<string> SizesAsync(Guid media) =>
        db.ScalarAsync<string>("select coalesce(string_agg(size || ':' || width || 'x' || height, ',' order by size), '') from platform.media_variant where media_id = @m", ("m", media));

    private static string Location(HttpResponseMessage response) =>
        response.Headers.Location.ShouldNotBeNull("a redirect") is { IsAbsoluteUri: true } absolute ? absolute.PathAndQuery : response.Headers.Location!.ToString();

    [Fact]
    public async Task A_public_pictures_copies_are_made_as_it_is_added_and_served_in_its_place()
    {
        RequireDatabase();
        long before = await db.ScalarAsync<long>("select count(*) from platform.media_object");

        // A phone photo on its side: copies of it as it's shown, 1,500 wide.
        Guid photo = (await AddAsync(TestImages.Jpeg(2000, 1500, orientation: 6), MediaVisibility.Public)).ShouldNotBeNull().MediaId;
        Guid narrow = (await AddAsync(TestImages.Png(1000, 600), MediaVisibility.Public)).ShouldNotBeNull().MediaId;
        Guid kept = (await AddAsync(TestImages.Jpeg(1600, 1200), MediaVisibility.Private)).ShouldNotBeNull().MediaId;
        try
        {
            (await SizesAsync(photo)).ShouldBe("480:480x640,1280:1280x1707");
            (await SizesAsync(narrow)).ShouldBe("480:480x288", "no wider than 1,280, so no copy that size");
            (await SizesAsync(kept)).ShouldBe("", "a private picture has none");
            (await db.ScalarAsync<string>("select string_agg((sized_at is not null)::text, ',' order by id) from platform.media_object where id = any(@ids)", ("ids", new[] { photo, narrow, kept })))
                .ShouldBe("true,true,false");
            (await db.ScalarAsync<string>("select detail ->> 'sizes' from platform.audit_event where action = 'media.added' and subject_ids && array[@m]", ("m", photo))).ShouldBe("480,1280");

            using Browser anyone = db.NewBrowser();
            foreach (int size in MediaSizes.All)
            {
                HttpResponseMessage served = await anyone.GetAsync($"/media/{photo:D}/{size}");
                served.StatusCode.ShouldBe(HttpStatusCode.OK);
                served.Content.Headers.ContentType!.MediaType.ShouldBe("image/webp");
                served.Headers.CacheControl!.ToString().ShouldContain("immutable");
                served.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
                served.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("sandbox");
                served.Headers.ETag.ShouldNotBeNull();
                byte[] bytes = await served.Content.ReadAsByteArrayAsync(Ct);
                bytes.ShouldBe(await db.ScalarAsync<byte[]>(
                    "select b.bytes from platform.media_blob b join platform.media_variant v on v.tenant_id = b.tenant_id and v.object_key = b.object_key where v.media_id = @m and v.size = @s",
                    ("m", photo), ("s", size)));
            }

            // No copy that size, for good: the original, and the browser may keep the redirect.
            HttpResponseMessage original = await anyone.GetAsync($"/media/{narrow:D}/1280");
            original.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            Location(original).ShouldBe($"/media/{narrow:D}");
            original.Headers.CacheControl!.ToString().ShouldContain("immutable");

            // Only the two sizes, and only of a public picture.
            (await anyone.GetAsync($"/media/{photo:D}/640")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await anyone.GetAsync($"/media/{kept:D}/480")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await anyone.GetAsync($"/media/{Guid.NewGuid():D}/480")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

            // A JPEG's headers around nonsense looked like a picture to D103; it doesn't decode, so it isn't kept.
            byte[] fake = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0x02, 0x58, 0x03, 0x20, 1, 1, 0x11, 0, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 0x56, 0xFF, 0xD9];
            (await AddAsync(fake, MediaVisibility.Public)).ShouldBeNull();
            (await AddAsync(fake, MediaVisibility.Private)).ShouldBeNull("nor a private one");
            (await db.ScalarAsync<long>("select count(*) from platform.media_object")).ShouldBe(before + 3);
        }
        finally
        {
            await RemoveAsync(photo, narrow, kept);
        }

        // Removed, its copies' bytes go with it.
        (await db.ScalarAsync<long>("select count(*) from platform.media_variant where media_id = any(@ids)", ("ids", new[] { photo, narrow }))).ShouldBe(0);
        (await db.ScalarAsync<long>("select count(*) from platform.media_blob where object_key like @a or object_key like @b",
            ("a", "%" + photo.ToString("N") + "%"), ("b", "%" + narrow.ToString("N") + "%"))).ShouldBe(0);
    }

    /// <summary>A clean, public picture as one added before D106 was: no copies, and not sized.</summary>
    private async Task<Guid> OldPictureAsync(byte[] bytes, int width, int height, string type)
    {
        Guid media = UuidV7.NewId();
        string key = "images/" + media.ToString("N") + (type == "image/png" ? ".png" : ".jpg");
        await db.ScalarAsync<int>(
            """
            with o as (
              insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, uploaded_by, visibility)
              values (@m, @t, @k, @c, @n, @h, @w, @ht, 'clean', (select id from identity.app_user order by id limit 1), 'public')
              returning 1)
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) select @t, @k, @c, @b, @h from o returning 1
            """,
            ("m", media), ("t", KettleSeed.TenantId), ("k", key), ("c", type), ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)),
            ("w", width), ("ht", height), ("b", bytes));
        return media;
    }

    [Fact]
    public async Task The_jobs_worker_sizes_the_public_pictures_added_before()
    {
        RequireDatabase();
        Guid old = await OldPictureAsync(TestImages.Png(1600, 1200), 1600, 1200, "image/png");
        byte[] nonsense = [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0x02, 0x58, 0x03, 0x20, 1, 1, 0x11, 0, 0xFF, 0xDA, 0x00, 0x08, 1, 1, 0, 0, 63, 0, 0x12, 0x34, 0x56, 0xFF, 0xD9];
        Guid broken = await OldPictureAsync(nonsense, 800, 600, "image/jpeg");
        try
        {
            // Not made yet: the original, but not for keeps.
            using Browser anyone = db.NewBrowser();
            HttpResponseMessage waiting = await anyone.GetAsync($"/media/{old:D}/480");
            waiting.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            Location(waiting).ShouldBe($"/media/{old:D}");
            waiting.Headers.CacheControl!.NoStore.ShouldBeTrue();

            for (int run = 0; run < 10 && await db.ScalarAsync<long>("select count(*) from platform.media_object where id = any(@ids) and sized_at is null", ("ids", new[] { old, broken })) > 0; run++)
            {
                await using AsyncServiceScope scope = Scope();
                IServiceProvider services = scope.ServiceProvider;
                MediaSizesHandler handler = new(
                    (MediaLibrary)services.GetRequiredService<IMediaLibrary>(), services.GetRequiredService<PlatformDbContext>(),
                    services.GetRequiredService<ITenantContext>(), NullLogger<MediaSizesHandler>.Instance);
                await handler.RunAsync(bookNext: false, Ct);
            }

            (await SizesAsync(old)).ShouldBe("480:480x360,1280:1280x960");
            (await anyone.GetAsync($"/media/{old:D}/480")).Content.Headers.ContentType!.MediaType.ShouldBe("image/webp");

            // One that doesn't decode is tried once, and the original stands in for good.
            (await SizesAsync(broken)).ShouldBe("");
            (await db.ScalarAsync<bool>("select sized_at is not null from platform.media_object where id = @m", ("m", broken))).ShouldBeTrue();
            HttpResponseMessage standIn = await anyone.GetAsync($"/media/{broken:D}/480");
            standIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            standIn.Headers.CacheControl!.ToString().ShouldContain("immutable");

            // Nothing left of these to do.
            await using AsyncServiceScope again = Scope();
            (await ((MediaLibrary)again.ServiceProvider.GetRequiredService<IMediaLibrary>()).MakeSizesAsync(old, Ct)).ShouldBe(SizingOutcome.Gone);
        }
        finally
        {
            await RemoveAsync(old, broken);
        }
    }
}
