using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrightTime.Web.Tests.Localisation;

/// <summary>
/// The public pages in each language, through the real pipeline on sample data: the words, the
/// direction, the date formats, and how the choice of language is made and kept.
/// </summary>
public sealed class LocalisedPagesTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client(bool cookies = false) => factory
        .WithWebHostBuilder(b => b.UseEnvironment("Development"))
        .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = cookies });

    private static async Task<(HttpResponseMessage Response, string Html)> GetAsync(HttpClient client, string path, string? acceptLanguage = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (acceptLanguage is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(acceptLanguage);
        }

        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        return (response, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("en", "ltr", "Find something to do")]
    [InlineData("es", "ltr", "Encuentra algo que hacer")]
    [InlineData("zh-Hans", "ltr", "找点事情做")]
    [InlineData("bn", "ltr", "করার মতো কিছু খুঁজুন")]
    [InlineData("ht", "ltr", "Jwenn yon bagay pou fè")]
    [InlineData("ar", "rtl", "ابحث عن شيء تفعله")]
    public async Task The_finder_speaks_each_launch_language_in_its_own_direction(string locale, string dir, string heading)
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/search?lang=" + locale);

        html.ShouldContain($"<html lang=\"{locale}\" dir=\"{dir}\">");
        html.ShouldContain($"<h1>{heading}</h1>");
    }

    [Fact]
    public async Task The_browsers_language_is_used_when_nothing_was_chosen()
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/search", "fr-CA, es;q=0.8");

        html.ShouldContain("<html lang=\"es\"");
    }

    [Fact]
    public async Task A_chosen_language_is_remembered_for_the_next_page()
    {
        using HttpClient client = Client(cookies: true);
        (HttpResponseMessage chosen, _) = await GetAsync(client, "/search?lang=ht");
        chosen.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith("brighttime.lang=ht", StringComparison.Ordinal));

        (_, string next) = await GetAsync(client, "/listings/robotics-club", "en");

        next.ShouldContain("<html lang=\"ht\"", Case.Sensitive, "the remembered choice beats the browser's language");
    }

    [Fact]
    public async Task Every_page_offers_each_language_by_its_own_name_and_marks_the_current_one()
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/search?lang=bn&q=art");

        html.ShouldContain("hreflang=\"ar\"");
        html.ShouldContain(">العربية</a>");
        html.ShouldContain(">Kreyòl ayisyen</a>");
        html.ShouldContain("href=\"/search?lang=es&amp;q=art\"", Case.Sensitive, "the page is kept, only the language changes");
        html.ShouldMatch("aria-current=\"true\"[^>]*>বাংলা</a>");
    }

    [Fact]
    public async Task Haitian_creole_dates_use_kreyol_names_not_ICU_placeholders()
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/listings/robotics-club?lang=ht");

        html.ShouldNotContain(" M1", Case.Sensitive, "ICU has no Haitian data and would print M10 for October");
        new[] { "lendi", "madi", "mèkredi", "jedi", "vandredi", "samdi", "dimanch" }.ShouldContain(d => html.Contains(d, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Arabic_counts_use_arabic_plural_forms()
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/search?lang=ar");

        // Six sample listings: Arabic's "few" form (3 to 10), not the singular and never English.
        html.ShouldContain("id=\"results-heading\">6 أنشطة</h2>");
    }

    [Fact]
    public async Task A_page_that_varies_by_language_says_so_to_caches()
    {
        using HttpClient client = Client();
        (HttpResponseMessage response, _) = await GetAsync(client, "/search");

        response.Headers.Vary.ShouldContain("Accept-Language");
    }

    [Fact]
    public async Task The_passkey_scripts_words_are_in_the_page_language_and_are_data()
    {
        using HttpClient client = Client();
        (_, string html) = await GetAsync(client, "/search?lang=es");

        html.ShouldContain("<script type=\"application/json\" id=\"passkey-text\">");
        html.ShouldContain("Demasiados intentos");
    }
}
