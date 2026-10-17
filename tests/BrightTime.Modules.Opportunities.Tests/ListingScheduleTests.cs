using BrightTime.Modules.Opportunities.Editing;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Opportunities.Projection;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>The schedule builder's newer parts: every N weeks, days off, and the show and take-down days.</summary>
public sealed class ListingScheduleTests
{
    private static readonly Guid Category = Guid.Parse("0196c3e8-0000-7000-8000-0000000000c1");
    private static readonly Guid Site = Guid.Parse("0196c3e8-0000-7000-8000-0000000000a1");

    private static ListingForm Valid() => new()
    {
        Kind = "program",
        Title = "Drone workshop",
        Summary = "Build and fly a drone.",
        TermIds = [Category],
        SiteIds = [Site],
        RegistrationMode = "external",
        ExternalUrl = "https://example.org/drones",
        Repeats = "weekly",
        Days = ["TU"],
        StartsOn = "2026-11-03",
        EndsOn = "2026-12-29",
        StartTime = "15:30",
        EndTime = "17:00",
    };

    private static (ParsedListing? Listing, IReadOnlyDictionary<string, Phrase[]> Errors) Parse(Action<ListingForm> change)
    {
        ListingForm form = Valid();
        change(form);
        return ListingFormParser.Parse(form);
    }

    [Fact]
    public void Every_week_keeps_the_rule_it_always_had() =>
        Parse(_ => { }).Listing!.Rrule.ShouldBe("FREQ=WEEKLY;BYDAY=TU");

    [Fact]
    public void Every_other_week_is_an_interval_in_the_rule() =>
        Parse(f => f.Interval = "2").Listing!.Rrule.ShouldBe("FREQ=WEEKLY;INTERVAL=2;BYDAY=TU");

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("two")]
    public void An_interval_outside_one_to_four_weeks_is_refused(string interval) =>
        Parse(f => f.Interval = interval).Errors.Keys.ShouldContain(nameof(ListingForm.Interval));

    [Fact]
    public void Every_other_week_meets_on_alternate_weeks()
    {
        ParsedListing listing = Parse(f => f.Interval = "2").Listing!;

        IReadOnlyList<Occurrence> sessions = ListingFormParser.Preview(listing, ListingEditor.Zone, new DateOnly(2026, 11, 1), 5);

        sessions.Select(o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.StartsAt, TimeZoneInfo.FindSystemTimeZoneById(ListingEditor.Zone)).DateTime))
            .ShouldBe([new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 17), new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 15), new DateOnly(2026, 12, 29)]);
    }

    [Fact]
    public void Days_off_are_read_one_per_line_or_by_comma_sorted_and_without_repeats()
    {
        ParsedListing listing = Parse(f => f.SkipDates = "2026-11-24\n2026-11-10, 2026-11-24").Listing!;

        listing.SkipDates.ShouldBe([new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 24)]);
    }

    [Fact]
    public void A_day_off_is_left_out_of_the_preview()
    {
        ParsedListing listing = Parse(f => f.SkipDates = "2026-11-10").Listing!;

        IReadOnlyList<Occurrence> sessions = ListingFormParser.Preview(listing, ListingEditor.Zone, new DateOnly(2026, 11, 1), 2);

        sessions.Select(o => o.StartsAt.Date.Day).ShouldBe([3, 17]);
    }

    [Theory]
    [InlineData("11/10/2026", "editor.error.skip_date_format")]
    [InlineData("2027-01-05", "editor.error.skip_date_outside")]
    [InlineData("2026-10-27", "editor.error.skip_date_outside")]
    public void A_day_off_must_be_a_date_inside_the_season(string value, string error) =>
        Parse(f => f.SkipDates = value).Errors[nameof(ListingForm.SkipDates)].Single().Key.ShouldBe(error);

    [Fact]
    public void A_season_cannot_skip_more_days_than_the_limit()
    {
        string many = string.Join('\n', Enumerable.Range(0, ListingFormParser.MaxSkipDates + 1).Select(_ => "2026-11-10"));

        Parse(f => f.SkipDates = many).Errors[nameof(ListingForm.SkipDates)].Single().Key.ShouldBe("editor.error.too_many_skip_dates");
    }

    [Fact]
    public void A_single_date_has_no_days_off_whatever_was_typed()
    {
        ParsedListing listing = Parse(f =>
        {
            f.Repeats = "once";
            f.SkipDates = "not a date";
        }).Listing!;

        listing.SkipDates.ShouldBeEmpty();
    }

    [Fact]
    public void Show_and_take_down_days_are_read()
    {
        ParsedListing listing = Parse(f =>
        {
            f.ShowFrom = "2026-10-20";
            f.TakeDownAfter = "2026-12-20";
        }).Listing!;

        listing.ShowFrom.ShouldBe(new DateOnly(2026, 10, 20));
        listing.TakeDownAfter.ShouldBe(new DateOnly(2026, 12, 20));
    }

    [Fact]
    public void It_cannot_come_down_before_it_goes_up() =>
        Parse(f =>
        {
            f.ShowFrom = "2026-12-21";
            f.TakeDownAfter = "2026-12-20";
        }).Errors[nameof(ListingForm.TakeDownAfter)].Single().Key.ShouldBe("editor.error.shown_before_taken_down");

    [Fact]
    public void An_event_cannot_start_showing_after_its_last_day() =>
        Parse(f =>
        {
            f.Kind = "event";
            f.ShowFrom = "2027-01-04";
        }).Errors[nameof(ListingForm.ShowFrom)].Single().Key.ShouldBe("editor.error.shown_before_taken_down");

    [Fact]
    public void A_published_listing_is_live_only_between_its_show_and_take_down_moments()
    {
        DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        ListingRecord listing = new() { Kind = "program", Slug = "x", Title = "x", Summary = "x", Status = "published" };

        ListingProjectionService.IsLive(listing, now).ShouldBeTrue();

        listing.PublishAt = now.AddHours(1);
        ListingProjectionService.IsLive(listing, now).ShouldBeFalse();
        ListingProjectionService.NextBoundary(listing, now).ShouldBe(now.AddHours(1));

        listing.PublishAt = now.AddHours(-1);
        listing.ExpiresAt = now.AddDays(3);
        ListingProjectionService.IsLive(listing, now).ShouldBeTrue();
        ListingProjectionService.NextBoundary(listing, now).ShouldBe(now.AddDays(3));

        listing.ExpiresAt = now;
        ListingProjectionService.IsLive(listing, now).ShouldBeFalse();
        ListingProjectionService.NextBoundary(listing, now).ShouldBeNull();
    }

    [Fact]
    public void A_listing_that_is_not_published_has_no_moment_to_wait_for()
    {
        DateTimeOffset now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        ListingRecord draft = new() { Kind = "program", Slug = "x", Title = "x", Summary = "x", Status = "draft", PublishAt = now.AddDays(1) };

        ListingProjectionService.NextBoundary(draft, now).ShouldBeNull();
    }
}
