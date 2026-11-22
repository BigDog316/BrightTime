using System.Net;
using BrightTime.Seed;
using BrightTime.Seed.Kettle;

namespace BrightTime.Web.Tests;

/// <summary>
/// The organiser's neighbourhood pages (D95): a page begun for a neighbourhood without one, not
/// published without an English section, each kind of section written (bad ones refused with what
/// was typed kept), moved, rewritten and removed, a Spanish version, then published to the
/// neighbourhood's public page in the reader's language and archived off it. A provider's admin
/// never gets in.
/// </summary>
/// <remarks>
/// Takes a neighbourhood no other test reads: the seeded pages and the one the finder tests expect
/// to have no page stay as they are.
/// </remarks>
[Collection(ConsoleDatabase.Name)]
public sealed class HoodPagesTests(ConsoleFixture db)
{
    private const string Organiser = KettleAccounts.OrganiserEmail;

    private void RequireDatabase() =>
        Assert.SkipUnless(db.IsAvailable, "Set BRIGHTTIME_TEST_CONNECTION to a PostgreSQL 18 + PostGIS superuser connection.");

    /// <summary>The page's text as a reader sees it: entities decoded, and the isolates around names dropped.</summary>
    private static string Shown(string html) => WebUtility.HtmlDecode(html).Replace("⁨", "", StringComparison.Ordinal).Replace("⁩", "", StringComparison.Ordinal);

    [Fact]
    public async Task An_organiser_writes_a_neighbourhood_page_publishes_it_in_two_languages_and_archives_it()
    {
        RequireDatabase();
        SeedArea hood = db.Data.Areas.Where(a => a.Kind == "neighbourhood" && !KettleCuration.PagedNeighbourhoods.Contains(a.Code) && a.Code != "hood-lakeview")
            .OrderBy(a => a.Code, StringComparer.Ordinal).First();
        string page = $"/organiser/neighbourhoods/{hood.Code}";
        string en = page + "?in=en";

        using Browser organiser = db.NewBrowser();
        await organiser.SignInAsync(Organiser);
        (await organiser.GetAsync("/organiser/neighbourhoods")).StatusCode.ShouldBe(HttpStatusCode.OK);
        organiser.Html.ShouldContain("href=\"/organiser/neighbourhoods\"", Case.Sensitive, "the console's nav links to it");
        organiser.Html.ShouldContain($"href=\"{page}\"");
        organiser.Html.ShouldNotContain("hoodpages.");

        // Begun as a draft; not published without a section in English.
        (await organiser.GetAsync(page)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Begin a page");
        (await organiser.PostAsync(page, "create")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=created");
        await organiser.GetAsync(en);
        organiser.Html.ShouldNotContain("hoodpages.");
        (await organiser.PostAsync(en, "publish")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Write a section in English before publishing the page.");

        // Text: refused without a body, the heading kept; then written.
        string text = en + "&add=rich_text";
        await organiser.GetAsync(text);
        (await organiser.PostAsync(text, "new", ("heading", "Welcome to the neighbourhood"), ("body", " "))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Write some text for the section, up to 4,000 characters.");
        organiser.Html.ShouldContain("value=\"Welcome to the neighbourhood\"", Case.Sensitive, "what was typed stays");
        await organiser.GetAsync(text);
        (await organiser.PostAsync(text, "new", ("heading", "Welcome to the neighbourhood"), ("body", "Start at the **library**.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=added");

        // Questions: one without an answer is refused; then two.
        string faq = en + "&add=faq";
        await organiser.GetAsync(faq);
        (await organiser.PostAsync(faq, "new", ("heading", ""), ("question-0", "Is it free?"), ("answer-0", "Mostly."), ("question-1", "Where?"), ("answer-1", "")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Each question needs an answer.");
        organiser.Html.ShouldContain("value=\"Where?\"");
        await organiser.GetAsync(faq);
        (await organiser.PostAsync(faq, "new", ("heading", ""), ("question-0", "Is it free?"), ("answer-0", "Mostly."), ("question-1", "Where do I start?"), ("answer-1", "At the library.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=added");

        // People to ask: a bad email is refused; then one person with a phone and an email.
        string people = en + "&add=contact_list";
        await organiser.GetAsync(people);
        (await organiser.PostAsync(people, "new", ("heading", "People to ask"), ("name-0", "Youth outreach"), ("email-0", "not an address")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Enter an email address like name@example.org.");
        await organiser.GetAsync(people);
        (await organiser.PostAsync(people, "new", ("heading", "People to ask"), ("name-0", "Youth outreach"), ("role-0", "Youth Bureau"), ("phone-0", "(914) 555-0199"), ("email-0", "outreach@kettle.example.org")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=added");

        // A list of programs, and a collection that's on the site.
        string list = en + "&add=listing_list";
        await organiser.GetAsync(list);
        (await organiser.PostAsync(list, "new", ("heading", ""), ("listingKind", "program"), ("limit", "30"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(organiser.Html).ShouldContain("Show from 1 to 24 activities.");
        await organiser.GetAsync(list);
        (await organiser.PostAsync(list, "new", ("heading", ""), ("listingKind", "program"), ("limit", "3"))).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=added");
        string collection = en + "&add=collection";
        await organiser.GetAsync(collection);
        organiser.Html.ShouldContain($"<option value=\"{KettleCuration.FreeCollection}\"");
        (await organiser.PostAsync(collection, "new", ("heading", "Free this month"), ("collection", KettleCuration.FreeCollection)))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=added");

        // The questions moved to the top, the text rewritten, and the list of programs removed.
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "up-1")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=saved");
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "block-1", ("heading", "Welcome to the neighbourhood"), ("body", "Start at the **library** on the square.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=saved");
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "remove-3")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=removed");
        (await db.ScalarAsync<long>(
            "select count(*) from opportunity.neighbourhood_page_block b join opportunity.neighbourhood_page p on p.id = b.page_id where p.neighbourhood_id = @h and b.locale = 'en'",
            ("h", hood.Id))).ShouldBe(4);
        (await db.ScalarAsync<string>(
            "select b.payload::text from opportunity.neighbourhood_page_block b join opportunity.neighbourhood_page p on p.id = b.page_id where p.neighbourhood_id = @h and b.block_kind = 'contact_list'",
            ("h", hood.Id))).ShouldBe("""{"contacts": [{"name": "Youth outreach", "role": "Youth Bureau", "email": "outreach@kettle.example.org", "phone": "(914) 555-0199"}]}""");

        // A Spanish version, whole.
        string es = page + "?in=es";
        await organiser.GetAsync(es + "&add=rich_text");
        (await organiser.PostAsync(es + "&add=rich_text", "new", ("heading", "Bienvenidos al barrio"), ("body", "Empieza en la biblioteca.")))
            .Headers.Location!.PathAndQuery.ShouldBe($"{es}&done=added");

        // Published: the sections on the neighbourhood's page, in order and in the reader's language.
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "publish")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=published");
        using Browser anyone = db.NewBrowser();
        string url = $"/neighbourhoods/{hood.Code}";
        (await anyone.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        string shown = Shown(anyone.Html);
        shown.ShouldContain("Welcome to the neighbourhood");
        anyone.Html.ShouldContain("<strong>library</strong>");
        shown.IndexOf("Where do I start?", StringComparison.Ordinal).ShouldBeLessThan(shown.IndexOf("Welcome to the neighbourhood", StringComparison.Ordinal), "the questions moved up");
        anyone.Html.ShouldContain("href=\"tel:9145550199\"");
        anyone.Html.ShouldContain("href=\"mailto:outreach@kettle.example.org\"");
        anyone.Html.ShouldContain($"href=\"/collections/{KettleCuration.FreeCollection}\"");
        shown.ShouldContain("Free this month");
        (await anyone.GetAsync(url + "?lang=es")).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(anyone.Html).ShouldContain("Bienvenidos al barrio");
        Shown(anyone.Html).ShouldNotContain("Welcome to the neighbourhood", Case.Sensitive, "a version is never mixed with the English");
        await anyone.GetAsync(url + "?lang=bn");
        Shown(anyone.Html).ShouldContain("Welcome to the neighbourhood", Case.Sensitive, "no Bengali version, so the English");

        // The list shows the page and its languages.
        await organiser.GetAsync("/organiser/neighbourhoods");
        Shown(organiser.Html).ShouldContain("English, Spanish");

        // Archived: the neighbourhood's page shows only what's on there.
        await organiser.GetAsync(en);
        (await organiser.PostAsync(en, "archive")).Headers.Location!.PathAndQuery.ShouldBe($"{en}&done=archived");
        (await anyone.GetAsync(url)).StatusCode.ShouldBe(HttpStatusCode.OK);
        Shown(anyone.Html).ShouldNotContain("Welcome to the neighbourhood");

        Guid pageId = await db.ScalarAsync<Guid>("select id from opportunity.neighbourhood_page where neighbourhood_id = @h", ("h", hood.Id));
        foreach (string action in new[] { "curation.neighbourhood_page_created", "curation.neighbourhood_page_changed", "curation.neighbourhood_page_published", "curation.neighbourhood_page_archived" })
        {
            (await db.ScalarAsync<long>("select count(*) from platform.audit_event where action = @a and subject_ids = array[@p]", ("a", action), ("p", pageId)))
                .ShouldBeGreaterThan(0, action);
        }

        // A provider's admin is turned away.
        using Browser provider = db.NewBrowser();
        await provider.SignInAsync($"admin@{db.Data.Organisations.OrderBy(o => o.Slug, StringComparer.Ordinal).First().Slug}.example.org");
        HttpResponseMessage refused = await provider.GetAsync(page);
        (refused.StatusCode == HttpStatusCode.Forbidden
         || (refused.StatusCode == HttpStatusCode.Redirect && refused.Headers.Location!.ToString().Contains("/access-denied", StringComparison.Ordinal))).ShouldBeTrue();
    }
}
