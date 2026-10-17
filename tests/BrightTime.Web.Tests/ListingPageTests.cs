using System.Net;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The public listing page and its calendar file, through the real pipeline on the sample
/// catalogue. These check what a visitor without JavaScript receives.
/// </summary>
public sealed class ListingPageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ListingPageTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private async Task<(HttpStatusCode Status, string Body, string? ContentType)> FetchAsync(string path)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, body, response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<string> GetAsync(string path)
    {
        (HttpStatusCode status, string body, _) = await FetchAsync(path);
        status.ShouldBe(HttpStatusCode.OK, $"GET {path}");
        return body;
    }

    [Fact]
    public async Task A_published_listing_renders_its_title_provider_and_registration_link()
    {
        string html = await GetAsync("/listings/robotics-club");

        html.ShouldContain("<h1 id=\"listing-title\" dir=\"auto\">Robotics club</h1>");
        html.ShouldContain("From <bdi>Kettle Youth Collective</bdi>");
        html.ShouldContain("<title>Robotics club · BrightTime</title>");
        html.ShouldContain("<meta name=\"description\" content=\"Build and program robots with a team every week.\"");

        // The hand-off is a plain link that tells a screen-reader user where it goes.
        html.ShouldContain("href=\"https://example.org/kettle-youth/robotics\" rel=\"external noopener noreferrer\"");
        html.ShouldContain("(opens the provider's website)");
        html.ShouldNotContain("target=\"_blank\"");
    }

    [Fact]
    public async Task An_unknown_slug_is_a_real_404()
    {
        (HttpStatusCode status, string body, _) = await FetchAsync("/listings/no-such-listing");

        status.ShouldBe(HttpStatusCode.NotFound);
        body.ShouldContain("Not Found");
    }

    [Fact]
    public async Task A_private_address_shows_only_the_neighbourhood()
    {
        string html = await GetAsync("/listings/youth-orchestra");

        html.ShouldContain("In the Westside area. The exact address is shared once you register.");
        html.ShouldNotContain("18 Linden Lane");
        html.ShouldNotContain("<address>");
    }

    [Fact]
    public async Task A_public_address_is_shown_in_full()
    {
        string html = await GetAsync("/listings/robotics-club");

        html.ShouldContain("<address>");
        html.ShouldContain("40 Orchard Hill Road");
    }

    [Fact]
    public async Task Upcoming_sessions_are_listed_with_machine_readable_times()
    {
        string html = await GetAsync("/listings/robotics-club");

        html.ShouldContain("<ol class=\"sessions\">");
        html.ShouldContain("<time datetime=\"");
        html.ShouldContain("class=\"calendar-link\" href=\"/listings/robotics-club/calendar.ics\"");
    }

    [Fact]
    public async Task Each_registration_mode_says_what_to_do()
    {
        (await GetAsync("/listings/evening-drop-in-basketball")).ShouldContain("No registration needed. Just come along.");
        (await GetAsync("/listings/youth-orchestra")).ShouldContain("Registration through BrightTime opens soon.");
        (await GetAsync("/listings/summer-camp-counsellor-paid")).ShouldContain("How to apply");
    }

    [Fact]
    public async Task A_deadline_is_shown()
    {
        string html = await GetAsync("/listings/youth-orchestra");

        html.ShouldContain("Dates to remember");
        html.ShouldContain("<strong>Registration closes</strong>");
    }

    [Fact]
    public async Task Prices_carry_a_dollar_sign_and_languages_their_own_names()
    {
        string orchestra = await GetAsync("/listings/youth-orchestra");
        orchestra.ShouldContain("$120.00");
        orchestra.ShouldNotContain("¤");
        (await GetAsync("/listings/teen-coding-lab")).ShouldContain("<dd>English, Espa&#xF1;ol</dd>");
    }

    [Fact]
    public async Task The_description_is_encoded_plain_text_in_paragraphs()
    {
        string html = await GetAsync("/listings/robotics-club");

        html.ShouldContain("<p dir=\"auto\">No experience needed. Laptops and kits are provided.</p>");
    }

    [Fact]
    public async Task Search_results_link_to_the_listing_page_and_keep_the_language()
    {
        (await GetAsync("/search")).ShouldContain("<a href=\"/listings/robotics-club\">Robotics club</a>");
        (await GetAsync("/search?lang=es")).ShouldContain("<a href=\"/listings/robotics-club?lang=es\">Robotics club</a>");
    }

    [Fact]
    public async Task The_page_is_rendered_in_the_requested_language_direction()
    {
        string html = await GetAsync("/listings/robotics-club?lang=ar");

        html.ShouldContain("dir=\"rtl\"");
        html.ShouldContain("href=\"/search?lang=ar\"");
    }

    [Fact]
    public async Task The_calendar_file_is_icalendar_with_one_event_per_session()
    {
        (HttpStatusCode status, string body, string? type) = await FetchAsync("/listings/robotics-club/calendar.ics");

        status.ShouldBe(HttpStatusCode.OK);
        type.ShouldBe("text/calendar");
        body.ShouldStartWith("BEGIN:VCALENDAR");
        body.ShouldContain("BEGIN:VEVENT");
        body.ShouldContain("SUMMARY:Robotics club");
        body.ShouldContain("40 Orchard Hill Road");
    }

    [Fact]
    public async Task The_calendar_file_never_carries_a_private_address()
    {
        string body = await GetAsync("/listings/youth-orchestra/calendar.ics");

        body.ShouldContain("LOCATION:Rehearsal studio\\, Westside");
        body.ShouldNotContain("Linden");
    }

    [Fact]
    public async Task The_calendar_file_for_an_unknown_slug_is_a_404()
    {
        (HttpStatusCode status, _, _) = await FetchAsync("/listings/no-such-listing/calendar.ics");

        status.ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// The page given hostile provider content directly, as a bad import might produce it.
/// </summary>
public sealed class ListingPageHostileContentTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly ListingDetail Hostile = new()
    {
        Id = new ListingId(Guid.Parse("0196c3e8-0000-7000-8000-0000000fbad1")),
        Slug = "hostile",
        Kind = ListingKind.Program,
        Title = "<b>Bold</b> title",
        Summary = "\"><script>alert(1)</script>",
        Description = "<img src=x onerror=alert(1)>\n\nSecond paragraph",
        OrganisationName = "Org",
        CostModel = CostModel.Free,
        Modality = Modality.InPerson,
        RegistrationMode = RegistrationMode.External,
        ExternalUrl = "javascript:alert(1)",
    };

    private readonly HttpClient _client;

    public ListingPageHostileContentTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _client = factory
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureTestServices(services =>
                    services.AddSingleton<IListingDetails>(new FixedListingDetails(Hostile)));
            })
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task Provider_text_is_encoded_and_a_script_url_is_never_linked()
    {
        string html = await _client.GetStringAsync(new Uri("/listings/hostile", UriKind.Relative), TestContext.Current.CancellationToken);

        html.ShouldNotContain("<script>alert(1)</script>");
        html.ShouldNotContain("<img src=x");
        html.ShouldNotContain("<b>Bold</b>");
        html.ShouldNotContain("javascript:");
        html.ShouldContain("&lt;img src=x onerror=alert(1)&gt;");
        html.ShouldContain("<p dir=\"auto\">Second paragraph</p>");
        html.ShouldContain("Contact Org to register.");
    }

    private sealed class FixedListingDetails(ListingDetail listing) : IListingDetails
    {
        public Task<ListingDetail?> GetPublishedAsync(TenantId tenant, string slug, string locale, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(slug, listing.Slug, StringComparison.Ordinal) ? listing : null);
    }
}
