using System.Globalization;
using System.Net;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BrightTime.Web.Tests;

/// <summary>
/// Pictures of listings (D105): a provider adds up to five, each saying what it shows; on a
/// published listing a new one waits with the pending edit until an organiser approves it, and
/// only then is it on the listing's page and, as the main picture, on its card. Removing one,
/// describing one and choosing the main one act at once; a discarded edit takes its pictures with
/// it; a clone shares its original's pictures, and a listing taken down keeps the ones waiting.
/// </summary>
/// <remarks>Uses Hollis Bend Community Garden, which no other test changes.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class ListingPicturesTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static string Location(HttpResponseMessage response)
    {
        Uri location = response.Headers.Location.ShouldNotBeNull("a redirect");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    private Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) => db.ScalarAsync<long>(sql, parameters);

    /// <summary>Runs the listing's projection, as the worker would.</summary>
    private Task ProjectAsync(Guid listing) => SeedProjection.ProjectAsync(db.Connection, KettleSeed.TenantId, [listing], NullLogger.Instance, Ct);

    private async Task<ListingForm> LoadFormAsync(Guid listingId)
    {
        await using AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
        return (await scope.ServiceProvider.GetRequiredService<ListingEditor>().LoadAsync(listingId, Ct))!.Form;
    }

    private Task<Guid> NewestPictureAsync(Guid listing) =>
        db.ScalarAsync<Guid>("select media_id from opportunity.listing_media where listing_id = @l order by id desc limit 1", ("l", listing));

    [Fact]
    public async Task A_published_listings_new_picture_waits_for_an_organiser_then_shows_on_its_page_and_card()
    {
        RequireDatabase();
        SeedOrganisation org = db.Data.Organisations.Single(o => o.Slug == "hollis-bend-community-garden");
        SeedListing seeded = db.Data.Listings.Where(l => l.OrgId == org.Id && l.Status == "published").OrderBy(l => l.Slug, StringComparer.Ordinal).First();
        string pictures = $"/provider/orgs/{org.Id}/listings/{seeded.Id}/pictures";
        string review = $"/organiser/review/listings/{seeded.Id}";

        using Browser provider = db.NewBrowser();
        await provider.SignInAsync($"admin@{org.Slug}.example.org");
        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/{seeded.Id}");
        provider.Html.ShouldContain($"href=\"{pictures}\"");
        await provider.GetAsync(pictures);
        Shown(provider.Html).ShouldContain("This listing has no pictures yet.");
        Shown(provider.Html).ShouldContain("New pictures wait for an organiser to approve them before families see them.");
        Shown(provider.Html).ShouldContain("Only show children whose families have agreed to their photo being used.");
        provider.Html.ShouldNotContain("editor.pictures.");

        // Refused: no description, and a photo too small; neither leaves anything behind.
        (await provider.PostFileAsync(pictures, "add", ("image", "beds.jpg", TestImages.Jpeg(1600, 900), "image/jpeg"), ("alt", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(provider.Html).ShouldContain("Say what the picture shows, in up to 250 characters.");
        await provider.GetAsync(pictures);
        await provider.PostFileAsync(pictures, "add", ("image", "small.png", TestImages.Png(600, 300), "image/png"), ("alt", "Beds"));
        Shown(provider.Html).ShouldContain("Choose a PNG or JPEG photo of up to 3 MB, at least 640 by 360 pixels.");
        (await CountAsync("select count(*) from opportunity.listing_media where listing_id = @l", ("l", seeded.Id))).ShouldBe(0);

        // Added to a published listing, it waits with a pending edit of nothing but itself.
        await provider.GetAsync(pictures);
        Location(await provider.PostFileAsync(pictures, "add", ("image", "planting.jpg", TestImages.Jpeg(1600, 900), "image/jpeg"), ("alt", "Two children planting seedlings")))
            .ShouldBe($"{pictures}?done=held");
        Guid planting = await NewestPictureAsync(seeded.Id);
        (await db.ScalarAsync<string>("select state from opportunity.listing_media where media_id = @m", ("m", planting))).ShouldBe("waiting");
        (await db.ScalarAsync<bool>("select hero_media_id is null from opportunity.listing where id = @l", ("l", seeded.Id))).ShouldBeTrue();
        (await db.ScalarAsync<string>("select state || ':' || (snapshot->'form'->>'title') from opportunity.listing_pending_edit where listing_id = @l", ("l", seeded.Id)))
            .ShouldBe("waiting:" + await db.ScalarAsync<string>("select title from opportunity.listing where id = @l", ("l", seeded.Id)));
        (await db.ScalarAsync<string>("select visibility from platform.media_object where id = @m", ("m", planting))).ShouldBe("public");
        await provider.GetAsync(pictures);
        Shown(provider.Html).ShouldContain("Waiting for an organiser");
        provider.Html.ShouldNotContain("Make this the main picture", Case.Sensitive, "a waiting picture can't be the main one");

        using Browser family = db.NewBrowser();
        await family.GetAsync($"/listings/{seeded.Slug}");
        family.Html.ShouldNotContain(planting.ToString("D"), Case.Insensitive, "families see nothing that waits");

        // The organiser sees it with the edit, and approves it.
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync("/organiser/review");
        organiser.Html.ShouldContain(review);
        await organiser.GetAsync(review);
        Shown(organiser.Html).ShouldContain("New pictures");
        organiser.Html.ShouldContain($"<img src=\"/media/{planting:D}\" alt=\"Two children planting seedlings\"");
        organiser.Html.ShouldNotContain("organiser.review.");
        Location(await organiser.PostAsync(review, "approve-edit")).ShouldEndWith("done=edit_approved");
        (await db.ScalarAsync<string>(
            "select m.state || ':' || (l.hero_media_id = m.media_id) from opportunity.listing_media m join opportunity.listing l on l.id = m.listing_id where m.media_id = @m",
            ("m", planting))).ShouldBe("live:true");
        (await CountAsync("select count(*) from opportunity.listing_pending_edit where listing_id = @l", ("l", seeded.Id))).ShouldBe(0);

        // Live: across the top of the page, and on the card in search.
        await ProjectAsync(seeded.Id);
        await family.GetAsync($"/listings/{seeded.Slug}");
        family.Html.ShouldContain($"<img class=\"listing-hero\" src=\"/media/{planting:D}\" alt=\"Two children planting seedlings\"");
        (await family.GetAsync($"/media/{planting:D}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        await family.GetAsync($"/organisations/{org.Slug}");
        family.Html.ShouldContain($"<img class=\"card-picture\" src=\"/media/{planting:D}\" alt=\"Two children planting seedlings\" loading=\"lazy\" />");

        // Described again: live at once, and the listing is among those changed since review.
        await provider.GetAsync(pictures);
        Location(await provider.PostAsync(pictures, "describe-0", ("alt", "Two children planting seedlings in a raised bed"))).ShouldBe($"{pictures}?done=described");
        (await db.ScalarAsync<string>("select alt_text from opportunity.listing_media where media_id = @m", ("m", planting))).ShouldBe("Two children planting seedlings in a raised bed");
        (await db.ScalarAsync<bool>("select updated_at > reviewed_at from opportunity.listing where id = @l", ("l", seeded.Id))).ShouldBeTrue();

        // A second waits; removed, it goes at once with its bytes, and so does the edit that held only it.
        await provider.GetAsync(pictures);
        await provider.PostFileAsync(pictures, "add", ("image", "beds.png", TestImages.Png(1200, 800), "image/png"), ("alt", "Raised beds in spring"));
        Guid beds = await NewestPictureAsync(seeded.Id);
        (await CountAsync("select count(*) from opportunity.listing_pending_edit where listing_id = @l", ("l", seeded.Id))).ShouldBe(1);
        await provider.GetAsync(pictures);
        Location(await provider.PostAsync(pictures, "remove-1")).ShouldBe($"{pictures}?done=removed");
        (await CountAsync("select count(*) from opportunity.listing_media where media_id = @m", ("m", beds))).ShouldBe(0);
        (await CountAsync("select count(*) from platform.media_object where id = @m", ("m", beds))).ShouldBe(0);
        (await CountAsync("select count(*) from opportunity.listing_pending_edit where listing_id = @l", ("l", seeded.Id))).ShouldBe(0);

        // Another waits; the provider discards the edit, and the picture goes with it.
        await provider.GetAsync(pictures);
        await provider.PostFileAsync(pictures, "add", ("image", "tools.jpg", TestImages.Jpeg(1024, 768), "image/jpeg"), ("alt", "Garden tools on a bench"));
        Guid tools = await NewestPictureAsync(seeded.Id);
        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/{seeded.Id}");
        Location(await provider.PostAsync($"/provider/orgs/{org.Id}/listings/{seeded.Id}", "discard-edit")).ShouldEndWith("?saved=discarded");
        (await CountAsync("select count(*) from platform.media_object where id = @m", ("m", tools))).ShouldBe(0);
        (await CountAsync("select count(*) from opportunity.listing_media where listing_id = @l", ("l", seeded.Id))).ShouldBe(1);

        (await db.ScalarAsync<string>(
            "select string_agg(action, ',' order by occurred_at) from platform.audit_event where @l = any(subject_ids) and action like 'listing.picture_%'", ("l", seeded.Id)))
            .ShouldBe("listing.picture_added,listing.picture_described,listing.picture_added,listing.picture_removed,listing.picture_added");

        // A clone shares the picture; taken off the clone, the original keeps it and its bytes.
        DateOnly startsOn = DateOnly.Parse(await db.ScalarAsync<string>("select min(starts_on)::text from opportunity.listing_session where listing_id = @l", ("l", seeded.Id)), CultureInfo.InvariantCulture);
        string clone = $"/provider/orgs/{org.Id}/listings/{seeded.Id}/clone";
        await provider.GetAsync(clone);
        string cloned = Location(await provider.PostAsync(clone, "clone", ("StartsOn", startsOn.AddDays(364).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("SeasonLabel", "Pictures")));
        Guid copy = Guid.Parse(cloned.Split('/')[^1].Split('?')[0]);
        (await db.ScalarAsync<string>(
            "select m.state || ':' || (l.hero_media_id = m.media_id) from opportunity.listing_media m join opportunity.listing l on l.id = m.listing_id where l.id = @c and m.media_id = @m",
            ("c", copy), ("m", planting))).ShouldBe("live:true");
        string copyPictures = $"/provider/orgs/{org.Id}/listings/{copy}/pictures";
        await provider.GetAsync(copyPictures);
        Location(await provider.PostAsync(copyPictures, "remove-0")).ShouldBe($"{copyPictures}?done=removed");
        (await db.ScalarAsync<bool>("select hero_media_id is null from opportunity.listing where id = @c", ("c", copy))).ShouldBeTrue();
        (await CountAsync("select count(*) from platform.media_object where id = @m", ("m", planting))).ShouldBe(1, "the original still shows it");

        // On a draft a picture is live at once, and the main one; reviewed when it's sent in.
        await provider.GetAsync(copyPictures);
        Location(await provider.PostFileAsync(copyPictures, "add", ("image", "harvest.jpg", TestImages.Jpeg(1600, 900), "image/jpeg"), ("alt", "A basket of tomatoes")))
            .ShouldBe($"{copyPictures}?done=added");
        Guid harvest = await NewestPictureAsync(copy);
        (await db.ScalarAsync<string>(
            "select m.state || ':' || (l.hero_media_id = m.media_id) from opportunity.listing_media m join opportunity.listing l on l.id = m.listing_id where m.media_id = @m",
            ("m", harvest))).ShouldBe("live:true");

        // Published, then given a picture that waits, then taken down: the picture stays, live,
        // to be reviewed before the listing is published again.
        ListingForm form = await LoadFormAsync(copy);
        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/{copy}");
        Location(await provider.PostAsync($"/provider/orgs/{org.Id}/listings/{copy}", "listing", ConsoleTests.Fields(form, "submit"))).ShouldEndWith("?submitted=1");
        await organiser.GetAsync($"/organiser/review/listings/{copy}");
        await organiser.PostAsync($"/organiser/review/listings/{copy}", "publish");
        (await db.ScalarAsync<string>("select status from opportunity.listing where id = @c", ("c", copy))).ShouldBe("published");
        await provider.GetAsync(copyPictures);
        await provider.PostFileAsync(copyPictures, "add", ("image", "path.jpg", TestImages.Jpeg(1600, 900), "image/jpeg"), ("alt", "A gravel path between beds"));
        Guid path = await NewestPictureAsync(copy);
        (await db.ScalarAsync<string>("select state from opportunity.listing_media where media_id = @m", ("m", path))).ShouldBe("waiting");
        await provider.GetAsync($"/provider/orgs/{org.Id}/listings/{copy}");
        await provider.PostAsync($"/provider/orgs/{org.Id}/listings/{copy}", "unpublish", ("reason", "Not running this season after all."));
        (await db.ScalarAsync<string>(
            "select l.status || ':' || m.state from opportunity.listing_media m join opportunity.listing l on l.id = m.listing_id where m.media_id = @m", ("m", path)))
            .ShouldBe("unpublished:live");
    }

    [Fact]
    public async Task A_listing_has_at_most_five_pictures_and_only_its_own_organisation_changes_them()
    {
        RequireDatabase();
        SeedOrganisation org = db.Data.Organisations.Single(o => o.Slug == "hollis-bend-community-garden");
        SeedListing original = db.Data.Listings.Where(l => l.OrgId == org.Id && l.Status == "published").OrderBy(l => l.Slug, StringComparer.Ordinal).Last();

        // A draft of its own: a clone of a seeded listing, which nothing else reads.
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync($"admin@{org.Slug}.example.org");
        string clone = $"/provider/orgs/{org.Id}/listings/{original.Id}/clone";
        await provider.GetAsync(clone);
        DateOnly startsOn = DateOnly.Parse(await db.ScalarAsync<string>("select min(starts_on)::text from opportunity.listing_session where listing_id = @l", ("l", original.Id)), CultureInfo.InvariantCulture);
        string cloned = Location(await provider.PostAsync(clone, "clone", ("StartsOn", startsOn.AddDays(728).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), ("SeasonLabel", "Five")));
        Guid draft = Guid.Parse(cloned.Split('/')[^1].Split('?')[0]);
        string pictures = $"/provider/orgs/{org.Id}/listings/{draft}/pictures";
        for (int i = 0; i < 5; i++)
        {
            await provider.GetAsync(pictures);
            Location(await provider.PostFileAsync(pictures, "add", ("image", $"p{i}.png", TestImages.Png(800, 600), "image/png"), ("alt", $"Picture {i}")))
                .ShouldBe($"{pictures}?done=added");
        }

        await provider.GetAsync(pictures);
        Shown(provider.Html).ShouldContain("Pictures on this listing: 5 of 5.");
        Shown(provider.Html).ShouldContain("This listing has 5 pictures. Remove one to add another.");
        provider.Html.ShouldNotContain("enctype=\"multipart/form-data\"");
        (await provider.PostFileAsync(pictures, "add", ("image", "p6.png", TestImages.Png(800, 600), "image/png"), ("alt", "One too many"))).StatusCode
            .ShouldNotBe(HttpStatusCode.Redirect, "with no form to add one, nothing is added");

        // The editor refuses a sixth too, whoever asks, and keeps no bytes for it.
        long media = await CountAsync("select count(*) from platform.media_object");
        await using (AsyncServiceScope scope = db.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(new TenantId(KettleSeed.TenantId), TimeZoneInfo.Utc, Locale.English);
            Result sixth = await scope.ServiceProvider.GetRequiredService<ListingEditor>().AddPictureAsync(
                new OrganisationId(org.Id), draft, TestImages.Png(800, 600), "One too many", new UserId(Guid.NewGuid()), Ct);
            sixth.Error!.Code.ShouldBe("listing_pictures.too_many");
        }

        (await CountAsync("select count(*) from platform.media_object")).ShouldBe(media);
        (await CountAsync("select count(*) from opportunity.listing_media where listing_id = @l", ("l", draft))).ShouldBe(5);

        // The main picture is the first added; another can be chosen.
        Guid second = await db.ScalarAsync<Guid>("select media_id from opportunity.listing_media where listing_id = @l and alt_text = 'Picture 1'", ("l", draft));
        await provider.GetAsync(pictures);
        Location(await provider.PostAsync(pictures, "main-1")).ShouldBe($"{pictures}?done=main");
        (await db.ScalarAsync<Guid>("select hero_media_id from opportunity.listing where id = @l", ("l", draft))).ShouldBe(second);
        await provider.GetAsync(pictures);
        provider.Html.IndexOf($"/media/{second:D}", StringComparison.Ordinal).ShouldBeLessThan(provider.Html.IndexOf("Picture 0", StringComparison.Ordinal), "the main one first");

        // Another organisation's staff see nothing and change nothing.
        SeedOrganisation other = db.Data.Organisations.Single(o => o.Slug == "mill-district-makerspace");
        using Browser stranger = db.NewBrowser();
        await stranger.SignInAsync($"admin@{other.Slug}.example.org");
        (await stranger.GetAsync($"/provider/orgs/{other.Id}/listings/{draft}/pictures")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync(pictures)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
