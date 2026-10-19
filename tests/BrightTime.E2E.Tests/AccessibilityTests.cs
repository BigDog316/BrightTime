using System.Text;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace BrightTime.E2E.Tests;

/// <summary>
/// The M1 gate: axe-core finds no WCAG 2.2 A or AA violation on any public route, in either
/// engine, and none on the core finder pages in any of the six locales.
/// </summary>
/// <remarks>
/// Routes come from <see cref="PublicRoutes"/>. Console pages are behind sign-in, so axe doesn't
/// see them; <see cref="KeyboardTests"/> drives their main flows.
/// </remarks>
[Collection(Site.Name)]
public sealed class AccessibilityTests(SiteFixture site)
{
    /// <summary>The WCAG rules axe tests automatically. Best-practice rules are left out.</summary>
    private static readonly AxeRunOptions Wcag = new()
    {
        RunOnly = new RunOnlyOptions
        {
            Type = "tag",
            Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa", "wcag22a", "wcag22aa"],
        },
    };

    public static TheoryData<string, string> Routes() => PublicRoutes.OnEachEngine(Engines.Chromium, Engines.WebKit);

    public static TheoryData<string, string> Locales()
    {
        TheoryData<string, string> data = [];
        foreach (string locale in new[] { "en", "es", "zh-Hans", "bn", "ht", "ar" })
        {
            foreach (string route in new[] { "/", "/search", "{listing}" })
            {
                data.Add(locale, route);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_public_route_has_no_WCAG_violations(string engine, string route)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);

        await CheckAsync(engine, Resolve(route));
    }

    [Theory]
    [MemberData(nameof(Locales))]
    public async Task A_finder_page_has_no_WCAG_violations_in_any_locale(string locale, string route)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);

        string path = Resolve(route);
        await CheckAsync(Engines.Chromium, path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "lang=" + locale);
    }

    /// <summary>The control: the harness reports a violation it is shown, so a clean run means clean pages.</summary>
    [Theory]
    [MemberData(nameof(Engines.All), MemberType = typeof(Engines))]
    public async Task The_check_finds_a_planted_violation(string engine)
    {
        Assert.SkipWhen(site.SkipReason is not null, site.SkipReason ?? string.Empty);

        await site.RunAsync(engine, async page =>
        {
            await page.GotoAsync("/");
            await page.EvaluateAsync("() => document.querySelector('main').insertAdjacentHTML('beforeend', '<img src=\"/favicon.png\"><button></button>')");
            AxeResult result = await page.RunAxe(Wcag);
            result.Violations.Select(v => v.Id).ShouldBe(["button-name", "image-alt"], ignoreOrder: true);
        });
    }

    private Task CheckAsync(string engine, string path) => site.RunAsync(engine, async page =>
    {
        IResponse? response = await page.GotoAsync(path);
        response.ShouldNotBeNull();
        response.Status.ShouldBe(path.Contains("no-such", StringComparison.Ordinal) ? 404 : 200, $"{engine} {path}");
        AxeResult result = await page.RunAxe(Wcag);
        result.Passes.ShouldNotBeEmpty($"axe ran no rules on {engine} {path}");
        result.Violations.ShouldBeEmpty(Describe(engine, path, result.Violations));
    });

    private string Resolve(string route) => PublicRoutes.Resolve(site, route);

    private static string Describe(string engine, string path, AxeResultItem[] violations)
    {
        var text = new StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{engine} {path}:");
        foreach (AxeResultItem v in violations)
        {
            text.AppendLine().Append(System.Globalization.CultureInfo.InvariantCulture, $"  [{v.Impact}] {v.Id}: {v.Help} ({v.HelpUrl})");
            foreach (AxeResultNode node in v.Nodes.Take(5))
            {
                text.AppendLine().Append(System.Globalization.CultureInfo.InvariantCulture, $"    {node.Target}  {node.Html}");
            }
        }

        return text.ToString();
    }
}
