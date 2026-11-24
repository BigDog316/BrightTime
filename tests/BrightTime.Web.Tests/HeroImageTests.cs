using System.Net;
using System.Text;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Tests;

namespace BrightTime.Web.Tests;

/// <summary>
/// Pictures on collections and neighbourhood pages (D103), kept in the database behind
/// IObjectStore: anything but a large enough PNG or JPEG is refused, a photo loses its place but
/// keeps its orientation, the page shows it with what it shows, <c>/media/{id}</c> serves it to
/// anyone for a year only while it's clean and public, a new picture replaces the old one's bytes,
/// and removing it takes them away.
/// </summary>
/// <remarks>Makes its own collection and page, so the seeded ones other tests read stay as they are.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class HeroImageTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task A_collection_gets_a_picture_that_is_served_described_replaced_and_removed()
    {
        RequireDatabase();
        SeedListing pick = db.Data.Listings.Where(l => l.Status == "published").GroupBy(l => l.Title, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).Select(g => g.Single()).OrderBy(l => l.Title, StringComparer.Ordinal).Last();
        string suffix = Guid.NewGuid().ToString("N")[..6];
        string slug = "murals-" + suffix;

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync("/organiser/collections");
        HttpResponseMessage created = await organiser.PostAsync("/organiser/collections", "create", ("title", "Murals " + suffix), ("slug", slug));
        string page = created.Headers.Location!.AbsolutePath;
        Guid id = Guid.Parse(page[(page.LastIndexOf('/') + 1)..]);
        string find = $"{page}?find={Uri.EscapeDataString(pick.Title)}";
        await organiser.GetAsync(find);
        await organiser.PostAsync(find, "add-" + pick.Id.ToString("N"));
        await organiser.GetAsync(page);
        await organiser.PostAsync(page, "publish");

        // Refused: no description; a drawing that can run script; a photo too small for the top of a page.
        await organiser.GetAsync(page);
        organiser.Html.ShouldNotContain("curation.");
        (await organiser.PostFileAsync(page, "hero", ("image", "mural.jpg", TestImages.Jpeg(1600, 1200), "image/jpeg"), ("alt", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Say what the picture shows, in up to 250 characters.");
        await organiser.GetAsync(page);
        await organiser.PostFileAsync(page, "hero", ("image", "mural.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "image/svg+xml"), ("alt", "A mural"));
        Shown(organiser.Html).ShouldContain("Choose a PNG or JPEG photo of up to 3 MB, at least 960 by 320 pixels.");
        await organiser.GetAsync(page);
        await organiser.PostFileAsync(page, "hero", ("image", "small.png", TestImages.Png(500, 200), "image/png"), ("alt", "A mural"));
        Shown(organiser.Html).ShouldContain("Choose a PNG or JPEG photo of up to 3 MB, at least 960 by 320 pixels.");
        (await db.ScalarAsync<bool>("select hero_media_id is null from opportunity.collection where id = @c", ("c", id))).ShouldBeTrue();

        // A phone photo, turned: its place goes, its orientation stays, and it's measured as shown.
        await organiser.GetAsync(page);
        (await organiser.PostFileAsync(page, "hero", ("image", "mural.jpg", TestImages.Jpeg(1600, 1200, orientation: 6), "image/jpeg"), ("alt", "Teens painting a mural")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=hero");
        Guid first = await db.ScalarAsync<Guid>("select hero_media_id from opportunity.collection where id = @c", ("c", id));
        (await db.ScalarAsync<string>(
            "select scan_state || ':' || visibility || ':' || width || 'x' || height || ':' || (exif_stripped_at is not null) from platform.media_object where id = @m", ("m", first)))
            .ShouldBe("clean:public:1200x1600:true");
        byte[] stored = await db.ScalarAsync<byte[]>(
            "select b.bytes from platform.media_blob b join platform.media_object m on m.tenant_id = b.tenant_id and m.object_key = b.object_key where m.id = @m", ("m", first));
        Encoding.Latin1.GetString(stored).ShouldNotContain("GPS");

        // On the site with what it shows; served to anyone, for a year, as nothing but an image.
        using Browser anyone = db.NewBrowser();
        await anyone.GetAsync($"/collections/{slug}");
        anyone.Html.ShouldContain($"<img class=\"hero-image\" src=\"/media/{first:D}\" alt=\"Teens painting a mural\"");
        HttpResponseMessage served = await anyone.GetAsync($"/media/{first:D}");
        served.StatusCode.ShouldBe(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        (await served.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(stored);
        served.Headers.CacheControl!.ToString().ShouldContain("immutable");
        served.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        served.Headers.ETag.ShouldNotBeNull();
        (await anyone.GetAsync($"/media/{Guid.NewGuid():D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Described in Spanish, the Spanish page says so.
        await organiser.GetAsync(page);
        await organiser.PostAsync(page, "wording-es", ("wordingTitle", "Murales"), ("wordingDescription", ""), ("wordingHeroAlt", "Jóvenes pintando un mural"));
        await anyone.GetAsync($"/collections/{slug}?lang=es");
        Shown(anyone.Html).ShouldContain("alt=\"Jóvenes pintando un mural\"");

        // Replaced: the old picture's bytes go, and so does the Spanish description of it.
        await organiser.GetAsync(page);
        (await organiser.PostFileAsync(page, "hero", ("image", "brushes.png", TestImages.Png(1200, 400), "image/png"), ("alt", "Paint brushes in a jar")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=hero");
        Guid second = await db.ScalarAsync<Guid>("select hero_media_id from opportunity.collection where id = @c", ("c", id));
        second.ShouldNotBe(first);
        (await db.ScalarAsync<long>("select count(*) from platform.media_object where id = @m", ("m", first))).ShouldBe(0);
        (await anyone.GetAsync($"/media/{first:D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await db.ScalarAsync<bool>("select hero_alt is null from opportunity.collection_translation where collection_id = @c and locale = 'es'", ("c", id))).ShouldBeTrue();

        // Only clean, public media is served.
        await db.ScalarAsync<int>("update platform.media_object set visibility = 'private' where id = @m returning 1", ("m", second));
        (await anyone.GetAsync($"/media/{second:D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await db.ScalarAsync<int>("update platform.media_object set visibility = 'public' where id = @m returning 1", ("m", second));
        (await anyone.GetAsync($"/media/{second:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Described again, then removed: off the page, and its bytes gone.
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "hero-alt", ("alt", "Jars of paint brushes"))).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=hero_described");
        (await db.ScalarAsync<string>("select hero_alt from opportunity.collection where id = @c", ("c", id))).ShouldBe("Jars of paint brushes");
        await organiser.GetAsync(page);
        (await organiser.PostAsync(page, "hero-remove")).Headers.Location!.PathAndQuery.ShouldBe($"{page}?done=hero_removed");
        (await db.ScalarAsync<long>("select count(*) from platform.media_object where id = @m", ("m", second))).ShouldBe(0);
        (await db.ScalarAsync<long>("select count(*) from platform.media_blob where object_key like @k", ("k", "%" + second.ToString("N") + "%"))).ShouldBe(0);
        await anyone.GetAsync($"/collections/{slug}");
        anyone.Html.ShouldNotContain("hero-image");

        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'media.added' and subject_ids && array[@a, @b]", ("a", first), ("b", second))).ShouldBe(2);
        (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = 'media.removed' and subject_ids && array[@a, @b]", ("a", first), ("b", second))).ShouldBe(2);
    }

    [Fact]
    public async Task A_neighbourhood_page_gets_a_picture_described_in_each_language()
    {
        RequireDatabase();
        SeedArea hood = db.Data.Areas.Where(a => a.Kind == "neighbourhood" && !KettleCuration.PagedNeighbourhoods.Contains(a.Code) && a.Code != "hood-lakeview")
            .OrderByDescending(a => a.Code, StringComparer.Ordinal).First();
        string page = $"/organiser/neighbourhoods/{hood.Code}";
        string en = page + "?in=en";
        string es = page + "?in=es";

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync(page);
        await organiser.PostAsync(page, "create");
        string text = en + "&add=rich_text";
        await organiser.GetAsync(text);
        await organiser.PostAsync(text, "new", ("heading", "Around the park"), ("body", "Things to do near the park."));
        await organiser.GetAsync(en);
        await organiser.PostAsync(en, "publish");

        await organiser.GetAsync(en);
        organiser.Html.ShouldNotContain("hoodpages.");
        (await organiser.PostFileAsync(en, "hero", ("image", "park.png", TestImages.Png(1400, 500), "image/png"), ("alt", "A footbridge over the creek")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=hero");
        Guid image = await db.ScalarAsync<Guid>(
            "select p.hero_media_id from opportunity.neighbourhood_page p join platform.neighbourhood n on n.id = p.neighbourhood_id where n.code = @c", ("c", hood.Code));

        // A description in Spanish; an empty one there falls back to the English, which can't be emptied.
        await organiser.GetAsync(es);
        organiser.Html.ShouldNotContain("hoodpages.");
        (await organiser.PostAsync(es, "hero-alt", ("alt", "Un puente sobre el arroyo"))).Headers.Location!.PathAndQuery.ShouldBe($"{es}&done=hero_described");
        (await db.ScalarAsync<string>(
            "select p.hero_alt->>'es' from opportunity.neighbourhood_page p join platform.neighbourhood n on n.id = p.neighbourhood_id where n.code = @c", ("c", hood.Code)))
            .ShouldBe("Un puente sobre el arroyo");
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "hero-alt", ("alt", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Say what the picture shows, in up to 250 characters.");

        using Browser anyone = db.NewBrowser();
        await anyone.GetAsync($"/neighbourhoods/{hood.Code}");
        anyone.Html.ShouldContain($"<img class=\"hero-image\" src=\"/media/{image:D}\" alt=\"A footbridge over the creek\"");
        await anyone.GetAsync($"/neighbourhoods/{hood.Code}?lang=es");
        anyone.Html.ShouldContain("alt=\"A footbridge over the creek\"", Case.Sensitive, "no Spanish sections, so the page and its picture are in English");
        (await anyone.GetAsync($"/media/{image:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "hero-remove")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=hero_removed");
        (await db.ScalarAsync<long>("select count(*) from platform.media_object where id = @m", ("m", image))).ShouldBe(0);
        (await anyone.GetAsync($"/media/{image:D}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
