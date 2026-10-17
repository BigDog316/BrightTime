using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Editing;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>The editor's parsing: what a provider typed, to a listing or to field messages.</summary>
public sealed class ListingFormParserTests
{
    private static readonly Guid Category = Guid.Parse("0196c3e8-0000-7000-8000-0000000000c1");
    private static readonly Guid Site = Guid.Parse("0196c3e8-0000-7000-8000-0000000000a1");

    private static ListingForm Valid() => new()
    {
        Kind = "program",
        Title = "  Drone workshop  ",
        Summary = "Build and fly a drone.",
        TermIds = [Category],
        SiteIds = [Site],
        RegistrationMode = "external",
        ExternalUrl = "https://example.org/drones",
        Repeats = "weekly",
        Days = ["TU", "TH"],
        StartsOn = "2026-11-02",
        EndsOn = "2026-12-10",
        StartTime = "15:30",
        EndTime = "17:00",
    };

    [Fact]
    public void A_complete_form_parses_and_is_trimmed()
    {
        (ParsedListing? listing, IReadOnlyDictionary<string, string[]> errors) = ListingFormParser.Parse(Valid());

        errors.ShouldBeEmpty();
        listing.ShouldNotBeNull();
        listing.Title.ShouldBe("Drone workshop");
        listing.Rrule.ShouldBe("FREQ=WEEKLY;BYDAY=TU,TH");
    }

    [Fact]
    public void A_weekly_start_moves_to_the_first_day_the_rule_names()
    {
        // 2026-11-02 is a Monday; the rule is Tuesdays and Thursdays. RFC 5545 would otherwise
        // make the Monday an extra session.
        ListingFormParser.Parse(Valid()).Listing!.StartsOn.ShouldBe(new DateOnly(2026, 11, 3));
    }

    [Fact]
    public void A_single_date_ends_on_the_day_it_starts()
    {
        ListingForm form = Valid();
        form.Repeats = "once";
        form.EndsOn = "";

        ParsedListing listing = ListingFormParser.Parse(form).Listing!;

        listing.Rrule.ShouldBeNull();
        listing.EndsOn.ShouldBe(listing.StartsOn);
    }

    [Fact]
    public void Errors_are_keyed_by_form_field_and_ordered_like_the_form()
    {
        ListingForm form = Valid();
        form.Title = "";
        form.EndTime = "14:00";
        form.SiteIds.Clear();
        form.CostModel = "fixed";

        (ParsedListing? listing, IReadOnlyDictionary<string, string[]> errors) = ListingFormParser.Parse(form);

        listing.ShouldBeNull();
        errors.Keys.ShouldBe(["Title", "SiteIds", "EndTime", "CostMin"]);
    }

    [Theory]
    [InlineData("120", 12_000)]
    [InlineData("$1,250.50", 125_050)]
    [InlineData("0", 0)]
    public void Dollar_amounts_become_cents(string typed, int cents)
    {
        ListingForm form = Valid();
        form.CostModel = "fixed";
        form.CostMin = typed;

        ListingFormParser.Parse(form).Listing!.CostCentsMin.ShouldBe(cents);
    }

    [Theory]
    [InlineData("12.345")]
    [InlineData("-5")]
    [InlineData("twelve")]
    public void Unusable_amounts_are_explained(string typed)
    {
        ListingForm form = Valid();
        form.CostModel = "fixed";
        form.CostMin = typed;

        ListingFormParser.Parse(form).Errors.ShouldContainKey("CostMin");
    }

    [Fact]
    public void A_price_typed_before_choosing_free_is_dropped_not_rejected()
    {
        ListingForm form = Valid();
        form.CostModel = "free";
        form.CostMin = "40";

        ListingFormParser.Parse(form).Listing!.CostCentsMin.ShouldBeNull();
    }

    [Fact]
    public void A_season_cannot_run_for_years()
    {
        ListingForm form = Valid();
        form.EndsOn = "2036-12-10";

        ListingFormParser.Parse(form).Errors.ShouldContainKey("EndsOn");
    }

    [Fact]
    public void Only_a_resource_may_run_without_an_end_date()
    {
        ListingForm program = Valid();
        program.EndsOn = "";
        ListingFormParser.Parse(program).Errors.ShouldContainKey("EndsOn");

        ListingForm resource = Valid();
        resource.Kind = "resource";
        resource.RegistrationMode = "none";
        resource.EndsOn = "";
        ParsedListing listing = ListingFormParser.Parse(resource).Listing!;
        listing.EndsOn.ShouldBeNull();
        listing.IsDropIn.ShouldBeTrue("a resource is drop-in by definition");
    }

    [Fact]
    public void An_unknown_option_value_is_an_error_not_a_default()
    {
        ListingForm form = Valid();
        form.Kind = "concert";

        ListingFormParser.Parse(form).Errors.ShouldContainKey("Kind");
    }

    [Fact]
    public void A_javascript_link_is_refused_as_a_registration_url()
    {
        ListingForm form = Valid();
        form.ExternalUrl = "javascript:alert(1)";

        ListingFormParser.Parse(form).Errors.ShouldContainKey("ExternalUrl");
    }

    [Fact]
    public void The_preview_lists_the_schedule_from_today_on()
    {
        ParsedListing listing = ListingFormParser.Parse(Valid()).Listing!;

        var sessions = ListingFormParser.Preview(listing, "America/New_York", new DateOnly(2026, 11, 10), 3);

        sessions.Select(s => s.StartsAt.ToOffset(TimeSpan.FromHours(-5)).Date.Day).ShouldBe([10, 12, 17]);
    }

    [Fact]
    public void Kinds_parse_from_their_database_values()
    {
        foreach (ListingKind kind in Enum.GetValues<ListingKind>())
        {
            ListingForm form = Valid();
            form.Kind = kind.ToDbValue();
            if (kind == ListingKind.Resource)
            {
                form.RegistrationMode = "none";
            }

            if (kind == ListingKind.Job)
            {
                form.CostModel = "stipend_paid";
            }

            ListingFormParser.Parse(form).Listing!.Kind.ShouldBe(kind);
        }
    }
}
