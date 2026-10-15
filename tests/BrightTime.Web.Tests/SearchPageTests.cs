using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrightTime.Web.Tests;

/// <summary>
/// The finder page, run through the real pipeline in development (sample data, no
/// database). These check what a visitor without JavaScript receives.
/// </summary>
public sealed class SearchPageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SearchPageTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<string> GetAsync(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}");
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_finder_is_server_rendered_with_a_plain_get_form()
    {
        // The form must work with JavaScript off, so it is a GET to the same page.
        string html = await GetAsync("/search");

        html.ShouldContain("<form method=\"get\" action=\"/search\"");
        html.ShouldContain("<h1>Find something to do</h1>");
    }

    [Fact]
    public async Task Results_are_present_in_the_html_without_any_script()
    {
        string html = await GetAsync("/search");

        html.ShouldContain("<h3>Robotics club</h3>");
        html.ShouldContain("<ol class=\"listing-list\">");
    }

    [Fact]
    public async Task Each_filter_narrows_the_results()
    {
        (await GetAsync("/search?q=robotics")).ShouldContain("<h3>Robotics club</h3>");
        (await GetAsync("/search?q=robotics")).ShouldNotContain("<h3>Youth orchestra</h3>");

        (await GetAsync("/search?free=1")).ShouldNotContain("<h3>Youth orchestra</h3>");
        (await GetAsync("/search?kind=job")).ShouldContain("<h3>Summer camp counsellor (paid)</h3>");
    }

    [Fact]
    public async Task A_malformed_field_is_explained_beside_it_and_linked_from_a_summary()
    {
        string html = await GetAsync("/search?age=99");

        html.ShouldContain("class=\"error-summary\"");
        html.ShouldContain("href=\"#age\"");
        html.ShouldContain("<p class=\"field-error\" id=\"err-age\">");
        html.ShouldContain("aria-invalid=\"true\"");
    }

    [Fact]
    public async Task A_clean_field_carries_no_invalid_marker()
    {
        // ARIA takes lowercase "true". An attribute reading "False" would be wrong, so it is omitted.
        string html = await GetAsync("/search");

        html.ShouldNotContain("aria-invalid");
    }

    [Fact]
    public async Task Arabic_sets_the_document_to_right_to_left()
    {
        string html = await GetAsync("/search?lang=ar");

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
    }

    [Fact]
    public async Task A_stale_page_link_restarts_at_the_first_page_and_says_so()
    {
        string html = await GetAsync("/search?after=garbage!!");

        html.ShouldContain("That page link has expired");
        html.ShouldContain("<h3>Robotics club</h3>");
    }

    [Fact]
    public async Task The_heading_describes_a_restart_as_a_full_list_not_a_later_page()
    {
        string html = await GetAsync("/search?after=garbage!!");

        html.ShouldContain("id=\"results-heading\">6 listings");
        html.ShouldNotContain("Showing 6");
    }

    [Fact]
    public async Task The_page_has_a_skip_link_to_the_main_landmark()
    {
        string html = await GetAsync("/search");

        html.ShouldContain("href=\"#main\"");
        html.ShouldContain("<main id=\"main\"");
    }

    [Fact]
    public async Task An_empty_result_says_so_plainly()
    {
        string html = await GetAsync("/search?q=zzzznotathing");

        html.ShouldContain("No listings match these filters.");
    }
}
