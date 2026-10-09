using BrightTime.Modules.Directory.Clearances;
using BrightTime.Modules.Directory.Contracts;

namespace BrightTime.Modules.Directory.Tests;

public sealed class ClearanceCurrencyTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static readonly IReadOnlyList<ClearanceKind> Required =
        ClearanceExtensions.YouthFacingMinimum;

    private static ClearanceState Cleared(ClearanceKind kind, DateOnly? expires) =>
        new(kind, ClearanceStatus.Cleared, expires);

    [Fact]
    public void Current_clearances_pass()
    {
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess(
            [
                Cleared(ClearanceKind.BackgroundCheck, new DateOnly(2027, 6, 1)),
                Cleared(ClearanceKind.MandatedReporterTraining, new DateOnly(2027, 1, 1)),
            ],
            Required,
            Today);

        assessment.IsCleared.ShouldBeTrue();
        assessment.EarliestExpiry.ShouldBe(new DateOnly(2027, 1, 1));
    }

    [Fact]
    public void A_clearance_with_no_expiry_stays_current()
    {
        ClearanceCurrency.Assess(
            [
                Cleared(ClearanceKind.BackgroundCheck, null),
                Cleared(ClearanceKind.MandatedReporterTraining, null),
            ],
            Required,
            Today)
            .IsCleared.ShouldBeTrue();
    }

    [Fact]
    public void An_expired_clearance_is_not_a_stale_pass_it_is_not_a_pass()
    {
        // The row may still read 'cleared'. The expiry date decides, which is
        // what lets the nightly job take a mentor out of youth-facing work
        // without anyone having to notice.
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess(
            [
                Cleared(ClearanceKind.BackgroundCheck, Today.AddDays(-1)),
                Cleared(ClearanceKind.MandatedReporterTraining, null),
            ],
            Required,
            Today);

        assessment.IsCleared.ShouldBeFalse();
        assessment.Expired.ShouldBe([ClearanceKind.BackgroundCheck]);
        assessment.Missing.ShouldBeEmpty();
    }

    [Fact]
    public void A_clearance_expiring_today_is_still_current() =>
        ClearanceCurrency.Assess(
            [
                Cleared(ClearanceKind.BackgroundCheck, Today),
                Cleared(ClearanceKind.MandatedReporterTraining, null),
            ],
            Required,
            Today)
            .IsCleared.ShouldBeTrue();

    [Fact]
    public void Never_obtained_and_lapsed_are_reported_separately()
    {
        // Different conversations with the person concerned, so the dashboard
        // must not merge them.
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess(
            [Cleared(ClearanceKind.BackgroundCheck, Today.AddDays(-30))],
            Required,
            Today);

        assessment.Expired.ShouldBe([ClearanceKind.BackgroundCheck]);
        assessment.Missing.ShouldBe([ClearanceKind.MandatedReporterTraining]);

        string explanation = ClearanceCurrency.Explain(assessment);
        explanation.ShouldContain("expired");
        explanation.ShouldContain("not on file");
    }

    [Theory]
    [InlineData(ClearanceStatus.NotStarted)]
    [InlineData(ClearanceStatus.Pending)]
    [InlineData(ClearanceStatus.NotCleared)]
    [InlineData(ClearanceStatus.Withdrawn)]
    public void A_clearance_that_is_not_a_pass_counts_as_missing(ClearanceStatus status)
    {
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess(
            [
                new ClearanceState(ClearanceKind.BackgroundCheck, status, null),
                Cleared(ClearanceKind.MandatedReporterTraining, null),
            ],
            Required,
            Today);

        assessment.IsCleared.ShouldBeFalse();
        assessment.Missing.ShouldContain(ClearanceKind.BackgroundCheck);
    }

    [Fact]
    public void A_conditional_clearance_counts_as_cleared()
    {
        // The organisation accepted the condition; adjudicating it is their
        // responsibility, not the platform's. It is flagged in reports.
        ClearanceCurrency.Assess(
            [
                new ClearanceState(ClearanceKind.BackgroundCheck, ClearanceStatus.Conditional, null),
                Cleared(ClearanceKind.MandatedReporterTraining, null),
            ],
            Required,
            Today)
            .IsCleared.ShouldBeTrue();
    }

    [Fact]
    public void Clearances_expiring_inside_the_warning_window_are_flagged_while_still_valid()
    {
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess(
            [
                Cleared(ClearanceKind.BackgroundCheck, Today.AddDays(20)),
                Cleared(ClearanceKind.MandatedReporterTraining, Today.AddDays(200)),
            ],
            Required,
            Today,
            warningDays: 45);

        assessment.IsCleared.ShouldBeTrue();
        assessment.ExpiringSoon.Select(e => e.Kind).ShouldBe([ClearanceKind.BackgroundCheck]);
    }

    [Fact]
    public void Requiring_nothing_passes_trivially() =>
        ClearanceCurrency.Assess([], [], Today).IsCleared.ShouldBeTrue();

    [Fact]
    public void An_empty_clearance_record_fails_every_requirement()
    {
        ClearanceCurrency.Assessment assessment = ClearanceCurrency.Assess([], Required, Today);

        assessment.IsCleared.ShouldBeFalse();
        assessment.Missing.ShouldBe(Required, ignoreOrder: true);
    }

    [Fact]
    public void The_youth_facing_minimum_includes_a_background_check_and_reporter_training() =>
        ClearanceExtensions.YouthFacingMinimum.ShouldBe(
            [ClearanceKind.BackgroundCheck, ClearanceKind.MandatedReporterTraining],
            ignoreOrder: true);

    [Fact]
    public void Every_kind_and_status_round_trips_through_its_database_value()
    {
        foreach (ClearanceKind kind in Enum.GetValues<ClearanceKind>())
        {
            ClearanceExtensions.TryKindFromDbValue(kind.ToDbValue(), out ClearanceKind parsed).ShouldBeTrue();
            parsed.ShouldBe(kind);
        }

        foreach (ClearanceStatus status in Enum.GetValues<ClearanceStatus>())
        {
            ClearanceExtensions.TryStatusFromDbValue(status.ToDbValue(), out ClearanceStatus parsed).ShouldBeTrue();
            parsed.ShouldBe(status);
        }
    }
}
