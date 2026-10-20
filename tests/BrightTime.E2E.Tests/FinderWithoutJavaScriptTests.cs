using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BrightTime.E2E.Tests;

/// <summary>
/// The finder in a real browser with JavaScript off (D7): search, filter, page through, open a
/// listing, take its calendar, save it and change language, using only links and forms.
/// </summary>
[Collection(Site.Name)]
public sealed partial class FinderWithoutJavaScriptTests(SiteFixture site)
{
    [Theory]
    [MemberData(nameof(Engines.All), MemberType = typeof(Engines))]
    public async Task A_family_can_search_filter_and_page_through_results(string engine)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(engine, async page =>
        {
            await page.GoAsync("/");
            await page.GetByRole(AriaRole.Searchbox).First.FillAsync("soccer");
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).First.ClickAsync();

            await page.ArrivedAsync(new Regex("/search\\?q=soccer"));
            int soccer = await ResultCountAsync(page);
            soccer.ShouldBeGreaterThan(0);

            // Narrow with the filter form, as a family would: tick "free", search again.
            await page.Locator("form.finder-form input[name=free]").CheckAsync();
            await page.Locator("form.finder-form button[type=submit]").ClickAsync();
            await page.ArrivedAsync(new Regex("[?&]free=1"));
            page.Url.ShouldContain("q=soccer");
            (await ResultCountAsync(page)).ShouldBeLessThanOrEqualTo(soccer);

            // Every published listing can be reached by following "Show more results".
            await page.GoAsync("/search");
            HashSet<string> seen = new(StringComparer.Ordinal);
            for (int pages = 0; pages < 40; pages++)
            {
                foreach (string link in await ListingLinksAsync(page))
                {
                    seen.Add(link).ShouldBeTrue($"{link} appeared on two pages");
                }

                ILocator next = page.Locator("a[rel=next]");
                if (await next.CountAsync() == 0)
                {
                    break;
                }

                // Each page's link is unique (a keyset cursor), so its URL marks the navigation done.
                string href = (await next.GetAttributeAsync("href")).ShouldNotBeNull();
                await next.ClickAsync();
                await page.ArrivedAsync(new Regex(Regex.Escape(href) + "$"));
            }

            seen.Count.ShouldBe(site.Data.Listings.Count(l =>
                l.Status == "published" && site.Data.Organisations.Single(o => o.Id == l.OrgId).Status == "approved"));
        }, javaScript: false);
    }

    [Theory]
    [MemberData(nameof(Engines.All), MemberType = typeof(Engines))]
    public async Task A_family_can_open_a_listing_save_it_and_take_its_calendar(string engine)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(engine, async page =>
        {
            await page.GoAsync("/search?q=soccer");
            ILocator first = page.Locator("ol.listing-list a[href^='/listings/']").First;
            string title = (await first.InnerTextAsync()).Trim();
            await first.ClickAsync();
            await page.ArrivedAsync(new Regex("/listings/[^/?]+$"));
            (await page.Locator("h1").InnerTextAsync()).Trim().ShouldBe(title);
            string listingPath = new Uri(page.Url).AbsolutePath;

            // The calendar link is a plain download.
            IDownload download = await page.RunAndWaitForDownloadAsync(() => page.Locator("a.calendar-link").ClickAsync());
            download.SuggestedFilename.ShouldEndWith(".ics");
            string? file = await download.PathAsync();
            file.ShouldNotBeNull();
            (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldContain("BEGIN:VEVENT");

            // Saving is a form post that comes back to the listing. The address doesn't change,
            // so wait for the redirected page itself, then for its state.
            ILocator save = page.Locator("form.save-form button[type=submit]");
            await page.RunAndWaitForResponseAsync(() => save.ClickAsync(), r => r.Url.EndsWith(listingPath, StringComparison.Ordinal) && r.Request.Method == "GET");
            await Assertions.Expect(save).ToHaveAttributeAsync("aria-pressed", "true");
            page.Url.ShouldEndWith(listingPath);

            await page.GoAsync("/saved");
            (await ListingLinksAsync(page)).ShouldContain(listingPath);
        }, javaScript: false);
    }

    [Theory]
    [MemberData(nameof(Engines.All), MemberType = typeof(Engines))]
    public async Task A_family_can_change_language_and_keep_it(string engine)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(engine, async page =>
        {
            await page.GoAsync("/search");
            await page.Locator("nav.language-picker a[hreflang=ar]").ClickAsync();
            await page.ArrivedAsync(new Regex("lang=ar"));
            (await page.Locator("html").GetAttributeAsync("lang")).ShouldBe("ar");
            (await page.Locator("html").GetAttributeAsync("dir")).ShouldBe("rtl");

            // The choice is remembered: the next page, with no lang in its address, stays in Arabic.
            await page.GoAsync("/neighbourhoods");
            (await page.Locator("html").GetAttributeAsync("lang")).ShouldBe("ar");

            await page.Locator("nav.language-picker a[hreflang=es]").ClickAsync();
            await page.ArrivedAsync(new Regex("lang=es"));
            (await page.Locator("html").GetAttributeAsync("dir")).ShouldBe("ltr");
            (await page.Locator("h1").InnerTextAsync()).ShouldNotBeNullOrWhiteSpace();
        }, javaScript: false);
    }

    private static async Task<int> ResultCountAsync(IPage page)
    {
        string heading = await page.Locator("#results-heading").InnerTextAsync();
        Match count = Digits().Match(heading);
        count.Success.ShouldBeTrue($"no count in \"{heading}\"");
        return int.Parse(count.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ListingLinksAsync(IPage page) =>
        [.. (await page.Locator("ol.listing-list a[href^='/listings/']").EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))"))
            .Distinct(StringComparer.Ordinal)];

    [GeneratedRegex("\\d+")]
    private static partial Regex Digits();
}
