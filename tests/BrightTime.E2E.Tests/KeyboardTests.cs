using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BrightTime.E2E.Tests;

/// <summary>
/// The keyboard half of the M1 accessibility gate: what axe cannot judge. Every public page
/// starts at a skip link that works, Tab reaches everything in order with a visible focus
/// indicator and never gets stuck, and a failed form opens on its errors, whose links land on
/// their fields. docs/accessibility/keyboard.md records the pass these tests keep true.
/// </summary>
/// <remarks>
/// Chromium only. WebKit follows Safari, where Tab skips links unless the user turns on "Press
/// Tab to highlight each item", so a Tab walk there tests a setting, not the site.
/// </remarks>
[Collection(Site.Name)]
public sealed partial class KeyboardTests(SiteFixture site)
{
    /// <summary>The focused element, described, with whether it shows a focus indicator and is on screen.</summary>
    private const string FocusScript = """
        () => {
          const e = document.activeElement;
          if (!e || e === document.body || e === document.documentElement) return null;
          const s = getComputedStyle(e);
          let ring = (s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) > 0) || (s.boxShadow && s.boxShadow !== 'none');
          // A map area shows focus as its shape's border instead (app.css, .hood-map).
          const hood = e.closest('.hood-map') && e.querySelector('.hood');
          if (hood) ring = parseFloat(getComputedStyle(hood).strokeWidth) >= 3;
          const r = e.getBoundingClientRect();
          const onScreen = r.width > 0 && r.height > 0 && r.bottom > 0 && r.right > 0
            && r.top < innerHeight && r.left < innerWidth && s.visibility !== 'hidden';
          const name = (e.getAttribute('aria-label') || e.innerText || e.value || e.name || e.id || '').trim().replace(/\s+/g, ' ').slice(0, 40);
          return { key: e.tagName.toLowerCase() + '|' + (e.id || '') + '|' + name + '|' + Math.round(r.x) + ',' + Math.round(r.y + scrollY),
                   label: e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + ' "' + name + '"',
                   ring, onScreen, inMain: !!e.closest('main') };
        }
        """;

    public static TheoryData<string> Routes()
    {
        TheoryData<string> data = [];
        foreach (string route in PublicRoutes.All)
        {
            data.Add(route);
        }

        return data;
    }

    public static TheoryData<bool> WithAndWithoutJavaScript => [true, false];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Tab_reaches_every_control_in_turn_with_visible_focus_and_no_trap(string route)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(Engines.Chromium, async page =>
        {
            await page.GoAsync(PublicRoutes.Resolve(site, route));
            List<string> problems = [];
            HashSet<string> seen = new(StringComparer.Ordinal);
            int stops = 0;
            for (; stops < 400; stops++)
            {
                await page.Keyboard.PressAsync("Tab");
                Stop? stop = await page.EvaluateAsync<Stop?>(FocusScript);
                if (stop is null)
                {
                    break; // Past the last control, out of the page: no trap.
                }

                if (!seen.Add(stop.Key))
                {
                    break; // Wrapped round to a stop already seen.
                }

                if (stops == 0 && !stop.Label.Contains("Skip to", StringComparison.Ordinal))
                {
                    problems.Add($"the first stop is {stop.Label}, not the skip link");
                }

                if (!stop.Ring)
                {
                    problems.Add($"no visible focus on {stop.Label}");
                }

                if (!stop.OnScreen)
                {
                    problems.Add($"focus off screen or hidden on {stop.Label}");
                }
            }

            stops.ShouldBeLessThan(400, $"{route}: Tab never left the page");
            problems.ShouldBeEmpty(route);
        });
    }

    [Theory]
    [MemberData(nameof(WithAndWithoutJavaScript))]
    public async Task The_skip_link_moves_focus_into_the_page_and_stays_on_it(bool javaScript)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(Engines.Chromium, async page =>
        {
            // Not the home page: a skip link resolved against <base href="/"> only fails elsewhere.
            await page.GoAsync("/search?q=soccer");
            await page.Keyboard.PressAsync("Tab");
            (await page.EvaluateAsync<string>("() => document.activeElement.className")).ShouldContain("skip-link");

            await page.Keyboard.PressAsync("Enter");
            await page.Keyboard.PressAsync("Tab");
            Stop? next = await page.EvaluateAsync<Stop?>(FocusScript);
            next.ShouldNotBeNull();
            next.InMain.ShouldBeTrue($"after the skip link, Tab went to {next.Label}");
            new Uri(page.Url).PathAndQuery.ShouldBe("/search?q=soccer");
        }, javaScript);
    }

    [Theory]
    [MemberData(nameof(WithAndWithoutJavaScript))]
    public async Task A_failed_form_opens_on_its_errors_and_each_links_to_its_field(bool javaScript)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(Engines.Chromium, async page =>
        {
            // A provider saves an empty listing by keyboard.
            var org = site.Data.Organisations.Where(o => o.Status == "approved").OrderBy(o => o.Slug, StringComparer.Ordinal).First();
            await page.GoAsync("/dev/sign-in");
            await TabToAsync(page, $"e => e.innerText.trim() === 'admin@{org.Slug}.example.org'");
            await page.Keyboard.PressAsync("Enter");
            await page.ArrivedAsync(new Regex("^(?!.*/dev/sign-in)"));

            string editor = $"/provider/orgs/{org.Id}/listings/new";
            await page.GoAsync(editor);
            await TabToAsync(page, "e => e.tagName === 'BUTTON' && e.value === 'save'");
            await page.Keyboard.PressAsync("Enter");

            ILocator summary = page.Locator("main .error-summary");
            await Assertions.Expect(summary).ToBeFocusedAsync();

            await page.Keyboard.PressAsync("Tab");
            string? target = await page.EvaluateAsync<string?>("() => document.activeElement.hash?.slice(1)");
            target.ShouldNotBeNullOrEmpty("the first Tab after the summary should be its first link");

            await page.Keyboard.PressAsync("Enter");
            // An attribute selector, because ids here can hold a dot (Site.NeighbourhoodId).
            await Assertions.Expect(page.Locator($"[id='{target}']")).ToBeFocusedAsync();
            new Uri(page.Url).AbsolutePath.ShouldBe(editor, "an error link must not leave the form");
        }, javaScript);
    }

    [Fact]
    public async Task A_search_with_a_malformed_filter_opens_on_what_to_fix()
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(Engines.Chromium, async page =>
        {
            await page.GoAsync("/search?age=99");
            await Assertions.Expect(page.Locator("main .error-summary")).ToBeFocusedAsync();
        }, javaScript: false);
    }

    [Fact]
    public async Task After_an_in_page_navigation_focus_is_on_the_new_page_heading()
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);
        await site.RunAsync(Engines.Chromium, async page =>
        {
            // A fresh load leaves focus at the top, where the skip link is.
            await page.GoAsync("/search?q=soccer");
            (await page.EvaluateAsync<string>("() => document.activeElement.tagName")).ShouldBe("BODY");

            // Blazor swaps the next page in without a reload; its heading takes focus, so a
            // screen reader announces it.
            await page.Locator("ol.listing-list a[href^='/listings/']").First.ClickAsync();
            await page.WaitForURLAsync(new Regex("/listings/[^/?]+$"));
            await Assertions.Expect(page.Locator("main h1")).ToBeFocusedAsync();
        });
    }

    private static async Task TabToAsync(IPage page, string predicate)
    {
        for (int i = 0; i < 400; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            if (await page.EvaluateAsync<bool>($"() => {{ const e = document.activeElement; return !!e && ({predicate})(e); }}"))
            {
                return;
            }
        }

        throw new ShouldAssertException("Not reachable by Tab: " + predicate);
    }

    /// <summary>One focus stop, as FocusScript describes it. Settable: Playwright fills it by property.</summary>
    private sealed class Stop
    {
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public bool Ring { get; set; }

        public bool OnScreen { get; set; }

        public bool InMain { get; set; }
    }
}
