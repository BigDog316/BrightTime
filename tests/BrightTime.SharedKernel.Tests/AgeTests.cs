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
