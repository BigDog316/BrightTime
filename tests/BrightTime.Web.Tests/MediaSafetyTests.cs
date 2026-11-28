using System.Net;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Media;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Media;
using BrightTime.Modules.Platform.Persistence;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace BrightTime.Web.Tests;

/// <summary>
/// Media safety (D123): every upload is virus-scanned as it came and made again from its pixels
/// before it's kept; one the scan finds something in, or can't check, isn't kept; and the jobs
/// worker scans what was kept before.
/// </summary>
[Collection(ConsoleDatabase.Name)]
public sealed class MediaSafetyTests(ConsoleFixture db)
{
    private static readonly ImageRules Rules = new(10 * 1024 * 1024, 1, 1, 6000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static AsyncServiceScope Scope(WebApplicationFactory<Program> factory)
    {
        AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        return scope;
    }

    private Task<Guid> SomeoneAsync() => db.ScalarAsync<Guid>("select id from identity.app_user order by id limit 1");

    private async Task<Result<StoredImage>> AddAsync(byte[] file, string visibility, WebApplicationFactory<Program>? factory = null)
    {
        UserId uploader = new(await SomeoneAsync());
        await using AsyncServiceScope scope = Scope(factory ?? db.Factory);
        return await scope.ServiceProvider.GetRequiredService<IMediaLibrary>().AddImageAsync(file, Rules, visibility, uploader, Ct);
    }

    private async Task RemoveAsync(params Guid[] media)
    {
        UserId actor = new(await SomeoneAsync());
        await using AsyncServiceScope scope = Scope(db.Factory);
        foreach (Guid id in media)
        {
            await scope.ServiceProvider.GetRequiredService<IMediaLibrary>().RemoveAsync(id, actor, Ct);
        }
    }

    private Task<byte[]> KeptAsync(Guid media) => db.ScalarAsync<byte[]>(
        "select b.bytes from platform.media_blob b join platform.media_object m on m.tenant_id = b.tenant_id and m.object_key = b.object_key where m.id = @m", ("m", media));

    [Fact]
    public async Task An_upload_is_made_again_from_its_pixels_upright_with_nothing_else_of_the_file_kept()
    {
        RequireDatabase();

        // A phone photo on its side, with a place in its EXIF and a comment; a PNG with a text chunk.
        byte[] photo = TestImages.Jpeg(2000, 1500, orientation: 6, marked: true);
        byte[] drawing = TestImages.Png(900, 700, text: "Drawn at 12 Elm Street");
        Guid photoId = (await AddAsync(photo, MediaVisibility.Private)).Value.MediaId;
        Guid drawingId = (await AddAsync(drawing, MediaVisibility.Public)).Value.MediaId;
        try
        {
            byte[] keptPhoto = await KeptAsync(photoId);
            keptPhoto.ShouldNotBe(photo);
            string asText = Encoding.Latin1.GetString(keptPhoto);
            asText.ShouldNotContain("Exif", customMessage: "no EXIF, so no place and no orientation");
            asText.ShouldNotContain("Taken at home", customMessage: "no comment");
            using (SKCodec codec = SKCodec.Create(new MemoryStream(keptPhoto)))
            {
                codec.EncodedFormat.ShouldBe(SKEncodedImageFormat.Jpeg);
                codec.EncodedOrigin.ShouldBe(SKEncodedOrigin.TopLeft, "turned as it's shown, so it needs no orientation");
                (codec.Info.Width, codec.Info.Height).ShouldBe((1500, 2000));
            }

            // The stored top-left quarter was black; shown, it's the top right.
            using (SKBitmap shown = SKBitmap.Decode(keptPhoto))
            {
                shown.GetPixel(1350, 200).Red.ShouldBeLessThan((byte)40);
                shown.GetPixel(150, 200).Red.ShouldBeGreaterThan((byte)100);
            }

            byte[] keptDrawing = await KeptAsync(drawingId);
            Encoding.Latin1.GetString(keptDrawing).ShouldNotContain("tEXt");
            TestImages.Pixels(keptDrawing).ShouldBe(TestImages.Pixels(drawing), "a PNG is made again without loss");

            (await db.ScalarAsync<string>("select string_agg(scan_detail || '|' || (scanned_at is not null), ',' order by id) from platform.media_object where id = any(@ids)",
                ("ids", new[] { photoId, drawingId })))
                .ShouldBe(string.Join(',', Enumerable.Repeat(MediaLibrary.KeptDetail(TestPatternScanner.Engine) + "|true", 2)));
            (await db.ScalarAsync<string>("select width || 'x' || height || ' ' || byte_size from platform.media_object where id = @m", ("m", photoId)))
                .ShouldBe($"1500x2000 {keptPhoto.Length}");
            (await db.ScalarAsync<string>("select detail ->> 'scan' from platform.audit_event where action = 'media.added' and subject_ids && array[@m]", ("m", drawingId)))
                .ShouldBe(TestPatternScanner.Engine);

            // What's served is what was kept.
            using Browser anyone = db.NewBrowser();
            (await (await anyone.GetAsync($"/media/{drawingId:D}")).Content.ReadAsByteArrayAsync(Ct)).ShouldBe(keptDrawing);
        }
        finally
        {
            await RemoveAsync(photoId, drawingId);
        }
    }

    [Fact]
    public async Task A_large_picture_is_kept_no_more_than_4096_on_its_longest_side()
    {
        RequireDatabase();
        Result<StoredImage> added = await AddAsync(TestImages.Png(6000, 3000), MediaVisibility.Public);
        StoredImage image = added.Value;
        try
        {
            (image.Width, image.Height).ShouldBe((4096, 2048));
            using SKCodec codec = SKCodec.Create(new MemoryStream(await KeptAsync(image.MediaId)));
            (codec.Info.Width, codec.Info.Height).ShouldBe((4096, 2048));
            (await db.ScalarAsync<string>("select coalesce(string_agg(size || ':' || width || 'x' || height, ',' order by size), '') from platform.media_variant where media_id = @m", ("m", image.MediaId)))
                .ShouldBe("480:480x240,1280:1280x640", "the copies are made from what's kept");
        }
        finally
        {
            await RemoveAsync(image.MediaId);
        }
    }

    [Fact]
    public async Task A_file_the_virus_scan_finds_something_in_is_refused_and_the_refusal_audited()
    {
        RequireDatabase();
        long before = await db.ScalarAsync<long>("select count(*) from platform.media_object");
        Guid uploader = await SomeoneAsync();

        // A whole picture, with the test file after its end: the cleaner would drop it, but the scan
        // reads the file as it came.
        byte[] carrying = [.. TestImages.Png(400, 300), .. TestImagesTestFile()];
        Result<StoredImage> refused = await AddAsync(carrying, MediaVisibility.Public);

        refused.IsSuccess.ShouldBeFalse();
        refused.Error.ShouldBe(MediaRefusals.Infected);
        MediaRefusals.Or(refused.Error, Error.Validation("feature.picture", "the feature's own words")).ShouldBe(MediaRefusals.Infected, "said the same everywhere");
        (await db.ScalarAsync<long>("select count(*) from platform.media_object")).ShouldBe(before, "nothing kept");
        string hash = Convert.ToHexStringLower(SHA256.HashData(carrying));
        (await db.ScalarAsync<string>(
            "select outcome || '|' || reason || '|' || (detail ->> 'signature') || '|' || (detail ->> 'engine') from platform.audit_event where action = @a and actor_id = @u and detail ->> 'sha256' = @h",
            ("a", MediaLibrary.InfectedAction), ("u", uploader), ("h", hash)))
            .ShouldBe($"deny|virus scan|{TestPatternScanner.Signature}|{TestPatternScanner.Engine}");
    }

    [Fact]
    public async Task When_the_scan_cannot_be_done_nothing_is_kept()
    {
        RequireDatabase();
        long before = await db.ScalarAsync<long>("select count(*) from platform.media_object");
        await using WebApplicationFactory<Program> down = db.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IVirusScanner>(new DownScanner())));

        Result<StoredImage> refused = await AddAsync(TestImages.Png(400, 300), MediaVisibility.Public, down);

        refused.Error.ShouldBe(MediaRefusals.Unchecked);
        refused.Error!.Kind.ShouldBe(ErrorKind.Unavailable);
        (await db.ScalarAsync<long>("select count(*) from platform.media_object")).ShouldBe(before, "never kept unchecked");

        // And what isn't a picture is still the feature's to say.
        Result<StoredImage> notOne = await AddAsync("GIF89a"u8.ToArray(), MediaVisibility.Public);
        MediaRefusals.Or(notOne.Error!, Error.Validation("feature.picture", "the feature's own words")).Code.ShouldBe("feature.picture");
    }

    /// <summary>A clean picture as one kept before D123 was: cleaned, not re-encoded, and not virus-scanned.</summary>
    private async Task<Guid> OldPictureAsync(byte[] bytes)
    {
        Guid media = UuidV7.NewId();
        string key = "images/" + media.ToString("N") + ".png";
        await db.ScalarAsync<int>(
            """
            with o as (
              insert into platform.media_object (id, tenant_id, object_key, content_type, byte_size, sha256, width, height, scan_state, scan_detail, uploaded_by, visibility, sized_at)
              values (@m, @t, @k, 'image/png', @n, @h, 400, 300, 'clean', @d, (select id from identity.app_user order by id limit 1), 'public', now())
              returning 1)
            insert into platform.media_blob (tenant_id, object_key, content_type, bytes, sha256) select @t, @k, 'image/png', @b, @h from o returning 1
            """,
            ("m", media), ("t", KettleSeed.TenantId), ("k", key), ("n", (long)bytes.Length), ("h", SHA256.HashData(bytes)),
            ("d", MediaLibrary.CheckedDetail), ("b", bytes));
        return media;
    }

    private static async Task<(int Clean, int Infected, int Failed)> ScanRunAsync(WebApplicationFactory<Program> factory)
    {
        await using AsyncServiceScope scope = Scope(factory);
        IServiceProvider services = scope.ServiceProvider;
        MediaScanHandler handler = new(
            (MediaLibrary)services.GetRequiredService<IMediaLibrary>(), services.GetRequiredService<PlatformDbContext>(),
            services.GetRequiredService<ITenantContext>(), NullLogger<MediaScanHandler>.Instance);
        return await handler.RunAsync(bookNext: false, Ct);
    }

    [Fact]
    public async Task The_jobs_worker_scans_what_was_kept_before_and_an_infected_one_is_served_nowhere()
    {
        RequireDatabase();
        byte[] fine = TestImages.Png(400, 300);
        Guid old = await OldPictureAsync(fine);
        Guid bad = await OldPictureAsync([.. TestImages.Png(400, 300), .. TestImagesTestFile()]);
        try
        {
            using Browser anyone = db.NewBrowser();
            (await anyone.GetAsync($"/media/{bad:D}")).StatusCode.ShouldBe(HttpStatusCode.OK, "served, as before D123, until it's scanned");

            // While the scanner is down, nothing changes, and the run says so.
            await using (WebApplicationFactory<Program> down = db.Factory.WithWebHostBuilder(b =>
                b.ConfigureTestServices(s => s.AddSingleton<IVirusScanner>(new DownScanner()))))
            {
                (await ScanRunAsync(down)).Failed.ShouldBe(1, "it stops at the first: the rest would fail the same way");
                (await db.ScalarAsync<long>("select count(*) from platform.media_object where id = any(@ids) and scanned_at is null", ("ids", new[] { old, bad }))).ShouldBe(2);
            }

            // The run takes the tenant's oldest first, and other tests' pictures may be waiting too.
            for (int run = 0; run < 20 && await db.ScalarAsync<long>("select count(*) from platform.media_object where id = any(@ids) and scanned_at is null", ("ids", new[] { old, bad })) > 0; run++)
            {
                (await ScanRunAsync(db.Factory)).Failed.ShouldBe(0);
            }

            (await db.ScalarAsync<string>("select scan_state || '|' || scan_detail || '|' || (scanned_at is not null) from platform.media_object where id = @m", ("m", old)))
                .ShouldBe($"clean|{MediaLibrary.ScannedLaterDetail(TestPatternScanner.Engine)}|true");
            (await KeptAsync(old)).ShouldBe(fine, "not made again: an object's bytes never change");
            (await db.ScalarAsync<string>("select scan_state from platform.media_object where id = @m", ("m", bad))).ShouldBe("infected");
            (await anyone.GetAsync($"/media/{bad:D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "served nowhere");
            (await anyone.GetAsync($"/media/{old:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await db.ScalarAsync<string>("select outcome || '|' || (detail ->> 'signature') from platform.audit_event where action = @a and subject_ids && array[@m]",
                ("a", MediaLibrary.InfectedAction), ("m", bad)))
                .ShouldBe($"deny|{TestPatternScanner.Signature}");

            // Nothing left of these to do.
            await using AsyncServiceScope again = Scope(db.Factory);
            (await ((MediaLibrary)again.ServiceProvider.GetRequiredService<IMediaLibrary>()).ScanStoredAsync(bad, Ct)).ShouldBe(ScanOutcome.Gone);
        }
        finally
        {
            await RemoveAsync(old, bad);
        }
    }

    private static byte[] TestImagesTestFile() => TestPatternScanner.TestFile.ToArray();

    private sealed class DownScanner : IVirusScanner
    {
        public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanResult(ScanVerdict.Failed, "down", "unreachable"));
    }
}
