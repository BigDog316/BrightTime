using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace BrightTime.E2E.Tests;

/// <summary>
/// Navigation waits that don't rest on Playwright's lifecycle events. With JavaScript off,
/// Chromium now and then never reports one of them (load, or DOMContentLoaded) for a page it has
/// plainly loaded: a trace showed the new document present and the wait still pending. So these
/// poll the address, then wait for the footer, which closes every page's markup.
/// </summary>
public static class Arrival
{
    /// <summary>After a click or a submit: the address matches and the new page is all there.</summary>
    public static async Task ArrivedAsync(this IPage page, Regex url)
    {
        ArgumentNullException.ThrowIfNull(page);
        await Assertions.Expect(page).ToHaveURLAsync(url);
        await Assertions.Expect(page.Locator("footer.site-footer")).ToBeAttachedAsync();
    }

    /// <summary>Opens a page and waits until it is all there.</summary>
    public static async Task GoAsync(this IPage page, string path)
    {
        ArgumentNullException.ThrowIfNull(page);
        await page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
        await Assertions.Expect(page.Locator("footer.site-footer")).ToBeAttachedAsync();
    }
}
