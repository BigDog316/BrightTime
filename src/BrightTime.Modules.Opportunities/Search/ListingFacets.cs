using BrightTime.Modules.Opportunities.Contracts;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>
/// The facts about a listing that the finder filters on, before they are
/// flattened into tokens.
/// </summary>
public sealed record FacetInput
{
    public required ListingKind Kind { get; init; }

    public Modality Modality { get; init; } = Modality.InPerson;

    public CostModel CostModel { get; init; } = CostModel.Free;

    public int? AgeMin { get; init; }

    public int? AgeMax { get; init; }

    public int? GradeMin { get; init; }

    public int? GradeMax { get; init; }

    /// <summary>Taxonomy codes, under the <c>category</c> vocabulary.</summary>
    public IReadOnlyCollection<string> Categories { get; init; } = [];

    /// <summary>Taxonomy codes, under the <c>interest</c> vocabulary.</summary>
    public IReadOnlyCollection<string> Interests { get; init; } = [];

    /// <summary>Taxonomy codes, under the <c>accommodation</c> vocabulary.</summary>
    public IReadOnlyCollection<string> Accommodations { get; init; } = [];

    public IReadOnlyCollection<string> Languages { get; init; } = [];

    /// <summary>Days of week the listing meets, 0 = Sunday.</summary>
    public IReadOnlyCollection<int> DaysOfWeek { get; init; } = [];

    public IReadOnlyCollection<Guid> NeighbourhoodIds { get; init; } = [];

    public Guid OrganisationId { get; init; }

    public bool MealsProvided { get; init; }

    public bool TransportProvided { get; init; }

    public bool IsDropIn { get; init; }
}

/// <summary>
/// Flattens a listing into the <c>text[]</c> facet array that the finder's one
/// GIN index serves.
/// </summary>
/// <remarks>
/// <para>
/// Every equality or set predicate in the filter list is folded into one array,
/// so a single index answers fourteen filter dimensions and the planner gets one
/// well-estimated access path rather than six marginal ones. Age and grade are
/// enumerated per year, which turns "is a fifteen-year-old eligible" into a
/// token containment check instead of a range predicate.
/// </para>
/// <para>
/// A listing with no age bounds is open to everyone, so it carries every age
/// token. Without that, a search for age 6 would silently exclude every listing
/// that never bothered to state a minimum. The same holds for grade.
/// </para>
/// </remarks>
public static class ListingFacets
{
    public const int MinAge = 0;
    public const int MaxAge = 30;
    public const int MinGrade = -1;
    public const int MaxGrade = 12;

    public static IReadOnlyList<string> Compute(FacetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        HashSet<string> tokens = new(StringComparer.Ordinal)
        {
            "kind:" + input.Kind.ToDbValue(),
            "mode:" + input.Modality.ToDbValue(),
            "org:" + input.OrganisationId.ToString("D"),
        };

        foreach (string category in input.Categories)
        {
            tokens.Add("cat:" + Normalise(category));
        }

        foreach (string interest in input.Interests)
        {
            tokens.Add("int:" + Normalise(interest));
        }

        foreach (string accommodation in input.Accommodations)
        {
            tokens.Add("accom:" + Normalise(accommodation));
        }

        foreach (string language in input.Languages)
        {
            tokens.Add("lang:" + Normalise(language));
        }

        foreach (int day in input.DaysOfWeek.Where(d => d is >= 0 and <= 6).Distinct())
        {
            tokens.Add("dow:" + day.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (Guid neighbourhood in input.NeighbourhoodIds)
        {
            tokens.Add("hood:" + neighbourhood.ToString("D"));
        }

        foreach (int age in AgesCovered(input.AgeMin, input.AgeMax))
        {
            tokens.Add("age:" + age.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (int grade in GradesCovered(input.GradeMin, input.GradeMax))
        {
            tokens.Add("grade:" + grade.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (input.CostModel == CostModel.Free)
        {
            tokens.Add("free");
        }

        if (input.MealsProvided)
        {
            tokens.Add("meals");
        }

        if (input.TransportProvided)
        {
            tokens.Add("transport");
        }

        if (input.IsDropIn)
        {
            tokens.Add("dropin");
        }

        if (input.Modality is Modality.Virtual or Modality.Hybrid)
        {
            tokens.Add("virtual_ok");
        }

        return [.. tokens.Order(StringComparer.Ordinal)];
    }

    internal static IEnumerable<int> AgesCovered(int? min, int? max) =>
        Covered(min, max, MinAge, MaxAge);

    internal static IEnumerable<int> GradesCovered(int? min, int? max) =>
        Covered(min, max, MinGrade, MaxGrade);

    private static IEnumerable<int> Covered(int? min, int? max, int absoluteMin, int absoluteMax)
    {
        int low = Math.Clamp(min ?? absoluteMin, absoluteMin, absoluteMax);
        int high = Math.Clamp(max ?? absoluteMax, absoluteMin, absoluteMax);

        return low > high ? [] : Enumerable.Range(low, high - low + 1);
    }

    /// <summary>Lowercase, trimmed, with no whitespace, so "After School" and "after-school" do not become two tokens.</summary>
    internal static string Normalise(string value) =>
        new string(value.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());
}
