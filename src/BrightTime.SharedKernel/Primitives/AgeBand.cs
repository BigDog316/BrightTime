namespace BrightTime.SharedKernel.Primitives;

/// <summary>
/// The coarsest age representation that still answers the questions the
/// platform needs to answer. Reporting, aggregation, search facets and
/// recommendation all use the band; the exact date of birth stays encrypted
/// and is decrypted only to evaluate an eligibility rule.
/// </summary>
public enum AgeBand
{
    Unknown = 0,
    Under6,
    Age6To8,
    Age9To12,
    Age13To14,
    Age15To17,
    Age18To24,
    Adult,
}

public static class AgeBandExtensions
{
    /// <summary>The database representation, matching <c>ck_app_user_age_band</c>.</summary>
    public static string ToDbValue(this AgeBand band) => band switch
    {
        AgeBand.Under6 => "under_6",
        AgeBand.Age6To8 => "6_8",
        AgeBand.Age9To12 => "9_12",
        AgeBand.Age13To14 => "13_14",
        AgeBand.Age15To17 => "15_17",
        AgeBand.Age18To24 => "18_24",
        AgeBand.Adult => "adult",
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Unknown age band has no database representation."),
    };

    public static AgeBand FromDbValue(string? value) => value switch
    {
        "under_6" => AgeBand.Under6,
        "6_8" => AgeBand.Age6To8,
        "9_12" => AgeBand.Age9To12,
        "13_14" => AgeBand.Age13To14,
        "15_17" => AgeBand.Age15To17,
        "18_24" => AgeBand.Age18To24,
        "adult" => AgeBand.Adult,
        _ => AgeBand.Unknown,
    };

    public static AgeBand ForAge(int ageYears) => ageYears switch
    {
        < 0 => AgeBand.Unknown,
        < 6 => AgeBand.Under6,
        < 9 => AgeBand.Age6To8,
        < 13 => AgeBand.Age9To12,
        < 15 => AgeBand.Age13To14,
        < 18 => AgeBand.Age15To17,
        < 25 => AgeBand.Age18To24,
        _ => AgeBand.Adult,
    };

    /// <summary>
    /// Whether COPPA's under-13 rules apply. Deliberately errs toward the
    /// stricter answer: an unknown age is treated as under 13, so a missing
    /// date of birth cannot be a route to an unprotected account.
    /// </summary>
    public static bool IsCoppaProtected(this AgeBand band) =>
        band is AgeBand.Unknown or AgeBand.Under6 or AgeBand.Age6To8 or AgeBand.Age9To12;

    /// <summary>
    /// 13 to 17: old enough to make an account and consent for themselves, not yet an adult. The
    /// ages a youth account is for.
    /// </summary>
    public static bool IsYouth(this AgeBand band) => band is AgeBand.Age13To14 or AgeBand.Age15To17;
}
