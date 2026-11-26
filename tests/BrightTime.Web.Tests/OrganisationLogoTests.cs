using System.Net;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Tests;

namespace BrightTime.Web.Tests;

/// <summary>
/// An organisation's logo (D109): its owners and admins add one in the console, live at once and
/// with its smaller copies; it's beside the name on the organisation's page and in its listings'
/// bylines, as decoration; a new one replaces the old, bytes and all; an organiser can take it down.
/// </summary>
/// <remarks>Uses Hollis Bend Community Garden, and leaves it with no logo.</remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class OrganisationLogoTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    private static string Location(HttpResponseMessage response)
    {
        Uri location = response.Headers.Location.ShouldNotBeNull("a redirect");
        return location.IsAbsoluteUri ? location.PathAndQuery : location.ToString();
    }

    private async Task<Guid?> LogoAsync(Guid org) =>
        await db.ScalarAsync<string>("select coalesce(logo_media_id::text, '') from directory.organisation where id = @o", ("o", org)) is { Length: > 0 } id
            ? Guid.Parse(id)
            : null;

    private Task<long> MediaCountAsync(Guid media) =>
        db.ScalarAsync<long>("select count(*) from platform.media_object where id = @m", ("m", media));

    [Fact]
    public async Task An_admin_adds_a_logo_shown_beside_the_name_and_an_organiser_can_take_it_down()
    {
        RequireDatabase();
        SeedOrganisation org = db.Data.Organisations.Single(o => o.Slug == "hollis-bend-community-garden");
        SeedListing listing = db.Data.Listings.Where(l => l.OrgId == org.Id && l.Status == "published").OrderBy(l => l.Slug, StringComparer.Ordinal).First();
        string profile = $"/provider/orgs/{org.Id}/profile";
        string review = $"/organiser/review/organisations/{org.Id}";

        using Browser admin = db.NewBrowser();
        await admin.SignInAsync($"admin@{org.Slug}.example.org");
        await admin.GetAsync(profile);
        Shown(admin.Html).ShouldContain("Your organisation has no logo yet.");
        Shown(admin.Html).ShouldContain("no more than four times as wide as it's tall");
        admin.Html.ShouldNotContain("provider.logo.");

        // Refused: too wide, and too small; neither leaves anything behind.
        const string refused = "Choose a PNG or JPEG of up to 2 MB, at least 200 pixels each way, and no more than four times as wide as it's tall.";
        (await admin.PostFileAsync(profile, "logo", ("logo", "banner.png", TestImages.Png(1000, 200), "image/png"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(admin.Html).ShouldContain(refused);
        await admin.GetAsync(profile);
        await admin.PostFileAsync(profile, "logo", ("logo", "tiny.png", TestImages.Png(150, 150), "image/png"));
        Shown(admin.Html).ShouldContain(refused);
        (await LogoAsync(org.Id)).ShouldBeNull();

        // Added: live at once, public, with a copy for cards, and audited as the organisation's.
        await admin.GetAsync(profile);
        Location(await admin.PostFileAsync(profile, "logo", ("logo", "logo.png", TestImages.Png(800, 400), "image/png"))).ShouldBe($"{profile}?logo=added");
        Guid first = (await LogoAsync(org.Id)).ShouldNotBeNull();
        (await db.ScalarAsync<string>("select visibility from platform.media_object where id = @m", ("m", first))).ShouldBe("public");
        (await db.ScalarAsync<long>("select count(*) from platform.media_variant where media_id = @m and size = 480", ("m", first))).ShouldBe(1);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'organisation.logo_set' and actor_org_id = @o and detail->>'media' = @m and detail->>'by' = 'provider'",
            ("o", org.Id), ("m", first.ToString()))).ShouldBe(1);
        await admin.GetAsync($"{profile}?logo=added");
        Shown(admin.Html).ShouldContain("The logo is saved.");
        admin.Html.ShouldContain($"<img class=\"org-logo\" src=\"/media/{first:D}/480\" alt=\"Your organisation&#x27;s logo\" />");
        admin.Html.ShouldContain("A new logo, in place of this one");

        // Beside the name it stands for, so not read out: on the organisation's page and in its listings' bylines.
        using Browser family = db.NewBrowser();
        await family.GetAsync($"/organisations/{org.Slug}");
        family.Html.ShouldContain($"<img class=\"org-logo\" src=\"/media/{first:D}/480\" alt />", Case.Sensitive, "an empty alt: Blazor writes it bare");
        await family.GetAsync($"/listings/{listing.Slug}");
        family.Html.ShouldContain($"<img class=\"org-logo org-logo-small\" src=\"/media/{first:D}/480\" alt />", Case.Sensitive, "an empty alt: Blazor writes it bare");
        (await family.GetAsync($"/media/{first:D}/480")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Replaced: the old one goes, bytes and all.
        Location(await admin.PostFileAsync(profile, "logo", ("logo", "logo.jpg", TestImages.Jpeg(600, 600), "image/jpeg"))).ShouldBe($"{profile}?logo=added");
        Guid second = (await LogoAsync(org.Id)).ShouldNotBeNull();
        second.ShouldNotBe(first);
        (await MediaCountAsync(first)).ShouldBe(0);
        (await db.ScalarAsync<long>("select count(*) from platform.media_blob where object_key like @k", ("k", $"%{first:N}%"))).ShouldBe(0);

        // The organiser sees it on the organisation's review page, and takes it down.
        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        await organiser.GetAsync(review);
        organiser.Html.ShouldContain($"<img class=\"org-logo\" src=\"/media/{second:D}/480\" alt=\"The organisation&#x27;s logo\" />");
        organiser.Html.ShouldNotContain("organiser.org.logo");
        Location(await organiser.PostAsync(review, "logo-remove")).ShouldBe($"{review}?saved=logo");
        (await LogoAsync(org.Id)).ShouldBeNull();
        (await MediaCountAsync(second)).ShouldBe(0);
        (await db.ScalarAsync<long>(
            "select count(*) from platform.audit_event where action = 'organisation.logo_removed' and actor_org_id is null and detail->>'media' = @m and detail->>'by' = 'organiser'",
            ("m", second.ToString()))).ShouldBe(1);
        await organiser.GetAsync($"{review}?saved=logo");
        Shown(organiser.Html).ShouldContain("The logo is taken down.");

        await family.GetAsync($"/organisations/{org.Slug}");
        family.Html.ShouldNotContain("org-logo");
        await admin.GetAsync(profile);
        Shown(admin.Html).ShouldContain("Your organisation has no logo yet.");
    }
}
