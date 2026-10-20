using BrightTime.SharedKernel.Primitives;

namespace BrightTime.SharedKernel.Tests;

public sealed class AgeTests
{
    [Theory]
    [InlineData("2010-06-15", "2026-06-14", 15)]  // day before the birthday
    [InlineData("2010-06-15", "2026-06-15", 16)]  // on the birthday
    [InlineData("2010-06-15", "2026-06-16", 16)]
    [InlineData("2008-02-29", "2026-02-28", 17)]  // leap-day birth, non-leap year
    [InlineData("2008-02-29", "2026-03-01", 18)]
    [InlineData("2026-01-01", "2026-01-01", 0)]
    public void YearsOn_counts_completed_years(string birth, string asOf, int expected) =>
        Age.YearsOn(DateOnly.Parse(birth, null), DateOnly.Parse(asOf, null)).ShouldBe(expected);

    [Fact]
    public void ToBirthMonth_drops_the_day()
    {
        // The day of birth is the part that identifies; the month is all the
        // age-band search needs.
        Age.ToBirthMonth(new DateOnly(2011, 9, 23)).ShouldBe(new DateOnly(2011, 9, 1));
    }

    [Theory]
    [InlineData(2013, 1, 2026, 1, AgeBand.Age9To12)]
    [InlineData(2013, 1, 2026, 2, AgeBand.Age13To14)]
    public void BandFromBirthMonth_resolves_to_the_younger_reading(
        int birthYear, int birthMonth, int asOfYear, int asOfMonth, AgeBand expected)
    {
        // Month granularity is ambiguous within the month. Resolving to the
        // younger band means a profile can never gain a capability through
        // rounding: the COPPA gate errs toward protection.
        Age.BandFromBirthMonth(
                new DateOnly(birthYear, birthMonth, 1),
                new DateOnly(asOfYear, asOfMonth, 15))
            .ShouldBe(expected);
    }

    [Theory]
    [InlineData(AgeBand.Unknown, true)]
    [InlineData(AgeBand.Under6, true)]
    [InlineData(AgeBand.Age9To12, true)]
    [InlineData(AgeBand.Age13To14, false)]
    [InlineData(AgeBand.Adult, false)]
    public void Unknown_age_is_treated_as_COPPA_protected(AgeBand band, bool expected) =>
        band.IsCoppaProtected().ShouldBe(expected);

    [Fact]
    public void Every_band_round_trips_through_its_database_value()
    {
        foreach (AgeBand band in Enum.GetValues<AgeBand>())
        {
            if (band == AgeBand.Unknown)
            {
                continue;
            }

            AgeBandExtensions.FromDbValue(band.ToDbValue()).ShouldBe(band);
        }
    }

    [Fact]
    public void The_next_band_change_is_the_first_day_the_band_differs_and_no_sooner()
    {
        // Every birth month over thirty years, from a fixed day: the band holds right up to the
        // change and is different on it, so a scheduled recompute lands on the right day.
        var asOf = new DateOnly(2026, 10, 19);
        for (DateOnly month = new(1996, 1, 1); month <= new DateOnly(2026, 10, 1); month = month.AddMonths(1))
        {
            AgeBand now = Age.BandFromBirthMonth(month, asOf);
            DateOnly? change = Age.NextBandChange(month, asOf);
            if (change is not { } day)
            {
                now.ShouldBe(AgeBand.Adult, month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
                continue;
            }

            day.ShouldBeGreaterThan(asOf);
            Age.BandFromBirthMonth(month, day.AddDays(-1)).ShouldBe(now, month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
            Age.BandFromBirthMonth(month, day).ShouldNotBe(now, month.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void A_child_born_in_June_2014_turns_13_on_the_last_day_of_June_2027()
    {
        // The younger reading: any day in June could be the birthday, so the 30th it is.
        Age.NextBandChange(new DateOnly(2014, 6, 1), new DateOnly(2026, 10, 19)).ShouldBe(new DateOnly(2027, 6, 30));
    }

    [Fact]
    public void Database_values_match_the_schema_check_constraint()
    {
        // ck_app_user_age_band lists these exactly. A mismatch is a runtime
        // constraint violation on insert, so pin it here.
        string[] expected = ["under_6", "6_8", "9_12", "13_14", "15_17", "18_24", "adult"];

        Enum.GetValues<AgeBand>()
            .Where(b => b != AgeBand.Unknown)
            .Select(b => b.ToDbValue())
            .ShouldBe(expected);
    }
}
