namespace BrightTime.Modules.Opportunities.Search;

/// <summary>
/// The complete set of inputs the finder's ranking may use. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A closed list, on purpose. Each field is a text match, a distance from an
/// anchor the user chose, time until a start, declared interests, or popularity.
/// Race, ethnicity, income, disability, accommodation need, immigration status,
/// and school performance are not here, and must not be added. Accommodation is
/// a filter the user applies, never a reason to rank one listing above another.
/// A test enumerates this type's properties and fails if one of those names
/// appears.
/// </para>
/// </remarks>
public sealed record RankingFeatures(
    double TextRank,
    double? DistanceMetres,
    double? DaysUntilStart,
    int InterestOverlap,
    double Popularity);

/// <summary>The ranking function, and the explanation shown beside each result.</summary>
public static class RelevanceScorer
{
    // Mirrors the weights in db/queries/search-listings.sql. Changing one here
    // without the other makes the reference query and the application disagree.
    public const double TextWeight = 0.45;
    public const double DistanceWeight = 0.20;
    public const double StartSoonWeight = 0.15;
    public const double InterestWeight = 0.12;
    public const double PopularityWeight = 0.08;

    public const double DistanceScaleMetres = 4000.0;
    public const double StartSoonScaleDays = 30.0;
    public const double MissingDistanceScore = 0.5;
    public const double MissingStartScore = 0.3;

    public static double Score(RankingFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        double text = Math.Min(features.TextRank * 4.0, 1.0);

        double distance = features.DistanceMetres is { } metres
            ? Math.Exp(-metres / DistanceScaleMetres)
            : MissingDistanceScore;

        double startSoon = features.DaysUntilStart is { } days
            ? Math.Exp(-Math.Max(days, 0.0) / StartSoonScaleDays)
            : MissingStartScore;

        double interest = Math.Min(features.InterestOverlap / 3.0, 1.0);
        double popularity = Math.Min(features.Popularity / 100.0, 1.0);

        return Math.Round(
            TextWeight * Math.Max(text, 0.0)
            + DistanceWeight * distance
            + StartSoonWeight * startSoon
            + InterestWeight * interest
            + PopularityWeight * Math.Max(popularity, 0.0),
            6);
    }

    /// <summary>
    /// The factors that moved the score, in plain terms, for the result card.
    /// The interface renders these, so a family can see why a listing was shown.
    /// </summary>
    public static IReadOnlyList<string> Explain(RankingFeatures features)
    {
        ArgumentNullException.ThrowIfNull(features);

        List<string> reasons = [];

        if (features.TextRank > 0)
        {
            reasons.Add("matches your search");
        }

        if (features.DistanceMetres is { } metres)
        {
            reasons.Add(metres < 1000
                ? $"{metres:0} m away"
                : $"{metres / 1000:0.0} km away");
        }

        if (features.DaysUntilStart is { } days)
        {
            reasons.Add(days < 1 ? "starts today" : $"starts in {Math.Ceiling(days):0} days");
        }

        if (features.InterestOverlap > 0)
        {
            reasons.Add(features.InterestOverlap == 1
                ? "matches one of your interests"
                : $"matches {features.InterestOverlap} of your interests");
        }

        return reasons;
    }
}
