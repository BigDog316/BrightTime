using BrightTime.Modules.Directory.Clearances;
using BrightTime.Modules.Directory.Contracts;

namespace BrightTime.Modules.Directory.Tests;

/// <summary>
/// The rules behind recording a clearance (D98): what a record must say, that only organisers
/// record for the whole county, that a current record counts when an expired one of the same kind
/// is also on file, and when the nightly sweep next runs.
/// </summary>
public sealed class StaffClearancesTests
{
    private static readonly DateOnly Today = new(2026, 11, 22);

    private static string? Code(ClearanceInput input, bool asOrganiser = false) => StaffClearances.Check(input, asOrganiser, Today)?.Code;

    [Fact]
    public void A_record_says_what_it_must()
    {
        Code(new("background_check", "cleared", Today.AddDays(-30), Today.AddYears(2), false)).ShouldBeNull();
        Code(new("background_check", "pending", null, null, false)).ShouldBeNull("only a passed clearance needs dates");
        Code(new("background_check", "conditional", Today.AddDays(-30), null, false)).ShouldBeNull("not every clearance expires");
        Code(new("tarot_reading", "cleared", Today, null, false)).ShouldBe("clearances.kind");
        Code(new("background_check", "expired", Today, null, false)).ShouldBe("clearances.status", "the sweep marks a lapse, not a person");
        Code(new("background_check", "cleared", null, null, false)).ShouldBe("clearances.completed_needed");
        Code(new("background_check", "cleared", Today.AddDays(1), null, false)).ShouldBe("clearances.completed_future");
        Code(new("background_check", "cleared", Today.AddDays(-30), Today.AddDays(-30), false)).ShouldBe("clearances.expiry_order");
        Code(new("background_check", "cleared", Today.AddDays(-400), Today.AddDays(-1), false)).ShouldBe("clearances.expiry_past");
    }

    [Fact]
    public void Only_an_organiser_records_for_the_whole_county()
    {
        ClearanceInput county = new("fingerprint", "cleared", Today.AddDays(-5), null, true);
        Code(county).ShouldBe("clearances.county_wide");
        Code(county, asOrganiser: true).ShouldBeNull();
    }

    [Fact]
    public void A_current_record_counts_whichever_order_they_are_on_file()
    {
        (string, string, DateOnly?)[] expiredFirst =
        [
            ("background_check", "expired", Today.AddDays(-3)),
            ("background_check", "cleared", Today.AddYears(1)),
            ("mandated_reporter_training", "cleared", null),
        ];
        StaffClearances.Assess(expiredFirst, Today).Ready.ShouldBeTrue();
        StaffClearances.Assess(expiredFirst.Reverse(), Today).Ready.ShouldBeTrue();

        ClearanceReadiness missing = StaffClearances.Assess([("background_check", "cleared", Today.AddDays(20))], Today);
        missing.Ready.ShouldBeFalse();
        missing.Missing.ShouldBe(["mandated_reporter_training"]);
        missing.ExpiringSoon.ShouldBe([("background_check", Today.AddDays(20))]);
        StaffClearances.Required.ShouldBe(["background_check", "mandated_reporter_training"]);
    }

    [Fact]
    public void The_sweep_runs_next_at_six_in_the_countys_morning()
    {
        TimeZoneInfo york = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 05:00 in New York (10:00 UTC): later that morning.
        ClearanceSweepHandler.NextRun(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), york)
            .ShouldBe(new DateTimeOffset(2026, 11, 22, 11, 0, 0, TimeSpan.Zero));

        // 07:00 in New York: the next morning, in UTC.
        DateTimeOffset next = ClearanceSweepHandler.NextRun(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero), york);
        next.ShouldBe(new DateTimeOffset(2026, 11, 23, 11, 0, 0, TimeSpan.Zero));
        next.Offset.ShouldBe(TimeSpan.Zero);

        // Across the spring change, still six local.
        ClearanceSweepHandler.NextRun(new DateTimeOffset(2027, 3, 13, 12, 0, 0, TimeSpan.Zero), york)
            .ShouldBe(new DateTimeOffset(2027, 3, 14, 10, 0, 0, TimeSpan.Zero));
    }
}
