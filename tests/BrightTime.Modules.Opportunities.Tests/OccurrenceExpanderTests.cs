using BrightTime.Modules.Opportunities.Search;

namespace BrightTime.Modules.Opportunities.Tests;

/// <summary>
/// Recurrence and daylight-saving arithmetic. The failure these guard against is
/// quiet: a 9pm session drifting to 8pm or 10pm for half the year.
/// </summary>
public sealed class OccurrenceExpanderTests
{
    private const string Ny = "America/New_York";

    private static SessionRule Weekly(DateOnly startsOn, DateOnly? endsOn = null, IReadOnlyList<DateOnly>? excluded = null) =>
        new(Ny, startsOn, endsOn, new TimeOnly(21, 0), new TimeOnly(22, 30),
            "FREQ=WEEKLY;BYDAY=FR", excluded ?? [], false);

    private static DateTimeOffset LocalWallClock(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(Ny));

    [Fact]
    public void A_weekly_session_keeps_its_wall_clock_time_across_the_spring_change()
    {
        // Friday 9pm New York, every week, across the 2026-03-08 transition.
        // Every occurrence must read 21:00 local, though the UTC offset changes.
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 2, 20)),
            new DateOnly(2026, 2, 20),
            new DateOnly(2026, 3, 27));

        occurrences.Count.ShouldBeGreaterThanOrEqualTo(5);

        foreach (Occurrence occurrence in occurrences)
        {
            LocalWallClock(occurrence.StartsAt).TimeOfDay.ShouldBe(new TimeSpan(21, 0, 0),
                $"occurrence at {occurrence.StartsAt:O} drifted off 21:00 local");
        }
    }

    [Fact]
    public void The_utc_offset_actually_changes_across_the_transition()
    {
        // Guard the guard: if the offsets were the same, the test above would
        // pass trivially and prove nothing.
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 2, 20)),
            new DateOnly(2026, 2, 27),
            new DateOnly(2026, 3, 13));

        // Outputs are UTC, so the offset itself is always zero. What changes is the
        // UTC hour the same wall-clock time maps to: 02:00 before the change, 01:00 after.
        occurrences.Select(o => o.StartsAt.ToUniversalTime().Hour).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public void Occurrences_resolve_to_the_correct_utc_hour_on_each_side_of_the_change()
    {
        // 21:00 EST is 02:00 UTC the next day; 21:00 EDT is 01:00 UTC.
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 2, 20)),
            new DateOnly(2026, 2, 27),
            new DateOnly(2026, 3, 13));

        Occurrence before = occurrences.First(o => o.StartsAt < new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero));
        Occurrence after = occurrences.First(o => o.StartsAt > new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero));

        before.StartsAt.ToUniversalTime().TimeOfDay.ShouldBe(new TimeSpan(2, 0, 0));
        after.StartsAt.ToUniversalTime().TimeOfDay.ShouldBe(new TimeSpan(1, 0, 0));
    }

    [Fact]
    public void A_local_time_in_the_spring_gap_moves_forward_an_hour()
    {
        // 02:30 on 2026-03-08 does not exist in New York. Moved to 03:30 EDT.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Ny);

        DateTimeOffset resolved = OccurrenceExpander.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), zone);

        LocalWallClock(resolved).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture).ShouldBe("03:30");
    }

    [Fact]
    public void A_local_time_in_the_autumn_overlap_takes_the_earlier_instant()
    {
        // 01:30 on 2026-11-01 happens twice. The first occurrence is EDT (-4),
        // so 05:30 UTC, not 06:30.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Ny);

        DateTimeOffset resolved = OccurrenceExpander.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0), zone);

        resolved.ToUniversalTime().TimeOfDay.ShouldBe(new TimeSpan(5, 30, 0));
    }

    [Fact]
    public void The_session_end_date_bounds_an_unbounded_rule()
    {
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 10, 2), endsOn: new DateOnly(2026, 10, 16)),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 12, 31));

        occurrences.Count.ShouldBe(3, "Fridays 2, 9 and 16 October only");
        occurrences.Max(o => LocalWallClock(o.StartsAt).Date).ShouldBe(new DateTime(2026, 10, 16));
    }

    [Fact]
    public void Excluded_dates_are_skipped()
    {
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 10, 2), endsOn: new DateOnly(2026, 10, 16), excluded: [new DateOnly(2026, 10, 9)]),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31));

        occurrences.Count.ShouldBe(2);
        occurrences.ShouldNotContain(o => LocalWallClock(o.StartsAt).Date == new DateTime(2026, 10, 9));
    }

    [Fact]
    public void Occurrences_outside_the_window_are_not_returned()
    {
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 10, 2)),
            new DateOnly(2026, 10, 10),
            new DateOnly(2026, 10, 17));

        occurrences.Count.ShouldBe(1);
        LocalWallClock(occurrences[0].StartsAt).Date.ShouldBe(new DateTime(2026, 10, 16));
    }

    [Fact]
    public void A_one_off_session_yields_exactly_its_start_when_inside_the_window()
    {
        var rule = new SessionRule(Ny, new DateOnly(2026, 10, 17), null, new TimeOnly(18, 0), new TimeOnly(19, 0), null, []);

        IReadOnlyList<Occurrence> inside = OccurrenceExpander.Expand(rule, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        IReadOnlyList<Occurrence> outside = OccurrenceExpander.Expand(rule, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        inside.Count.ShouldBe(1);
        outside.ShouldBeEmpty();
    }

    [Fact]
    public void An_end_before_the_start_is_a_caller_error()
    {
        Should.Throw<ArgumentException>(() =>
            OccurrenceExpander.Expand(Weekly(new DateOnly(2026, 10, 2)), new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1)));
    }

    [Fact]
    public void Each_occurrence_reports_its_day_of_week_with_sunday_as_zero()
    {
        // The schema's listing_occurrence.day_of_week is 0 = Sunday. A Friday must
        // read 5, not 6, or the dow:5 facet would point at the wrong day.
        IReadOnlyList<Occurrence> occurrences = OccurrenceExpander.Expand(
            Weekly(new DateOnly(2026, 10, 2), endsOn: new DateOnly(2026, 10, 9)),
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31));

        occurrences.ShouldAllBe(o => o.DayOfWeek == 5);
    }

    [Fact]
    public void A_malformed_rule_is_rejected_rather_than_silently_expanding_to_nothing()
    {
        var rule = new SessionRule(Ny, new DateOnly(2026, 10, 2), null, new TimeOnly(18, 0), new TimeOnly(19, 0), "FREQ=NONSENSE", []);

        Should.Throw<Exception>(() =>
            OccurrenceExpander.Expand(rule, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
    }
}
