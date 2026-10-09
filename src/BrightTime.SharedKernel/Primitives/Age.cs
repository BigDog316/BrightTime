namespace BrightTime.SharedKernel.Primitives;

/// <summary>Age arithmetic, in one place, because off-by-one birthdays are an eligibility bug.</summary>
public static class Age
{
    /// <summary>Completed years between <paramref name="dateOfBirth"/> and <paramref name="asOf"/>.</summary>
    /// <remarks>
    /// A 29 February birthday falls on 1 March in a non-leap year, not 28
    /// February. <see cref="DateOnly.AddYears(int)"/> clamps to the 28th, which
    /// would make a leap-day child turn 13 — or 18 — a day early. Age gates
    /// capabilities here (COPPA at 13, youth-facing work at 18), so the rule is
    /// the same one the age bands use: never round in the direction that grants
    /// something sooner.
    /// </remarks>
    public static int YearsOn(DateOnly dateOfBirth, DateOnly asOf)
    {
        int years = asOf.Year - dateOfBirth.Year;

        if (asOf < AnniversaryIn(dateOfBirth, asOf.Year))
        {
            years--;
        }

        return years;
    }

    /// <summary>The date a birthday falls on in a given year.</summary>
    internal static DateOnly AnniversaryIn(DateOnly dateOfBirth, int year)
    {
        if (dateOfBirth.Month == 2 && dateOfBirth.Day == 29 && !DateTime.IsLeapYear(year))
        {
            return new DateOnly(year, 3, 1);
        }

        return new DateOnly(year, dateOfBirth.Month, dateOfBirth.Day);
    }

    /// <summary>
    /// The month-granularity value stored in the clear (<c>app_user.birth_month</c>,
    /// <c>family_member.birth_month</c>). Enough for age-band search; not enough
    /// to be a date of birth.
    /// </summary>
    public static DateOnly ToBirthMonth(DateOnly dateOfBirth) =>
        new(dateOfBirth.Year, dateOfBirth.Month, 1);

    /// <summary>
    /// Age band from the month of birth alone. Ambiguous within a single month,
    /// so it reports the band for the <em>younger</em> reading: a profile must
    /// never gain a capability because of rounding.
    /// </summary>
    public static AgeBand BandFromBirthMonth(DateOnly birthMonth, DateOnly asOf)
    {
        DateOnly latestPossibleBirthDate = birthMonth.AddMonths(1).AddDays(-1);
        return AgeBandExtensions.ForAge(Math.Max(YearsOn(latestPossibleBirthDate, asOf), 0));
    }
}
