namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>
/// The listing editor's fields, exactly as typed. Numbers and dates stay text so a form that
/// comes back with an error shows what the provider entered, not what it parsed to.
/// </summary>
/// <remarks>
/// Each property's name is the form field's name; error keys use the same names, so an
/// error lands beside the field that caused it.
/// </remarks>
public sealed class ListingForm
{
    public string Kind { get; set; } = "program";

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string PrimaryLanguage { get; set; } = "en";

    public string AgeMin { get; set; } = string.Empty;

    public string AgeMax { get; set; } = string.Empty;

    public string GradeMin { get; set; } = string.Empty;

    public string GradeMax { get; set; } = string.Empty;

    /// <summary>Categories and interests.</summary>
    public IList<Guid> TermIds { get; init; } = [];

    public IList<Guid> AccommodationIds { get; init; } = [];

    public IList<string> Languages { get; init; } = [];

    public string Modality { get; set; } = "in_person";

    public string VirtualUrl { get; set; } = string.Empty;

    public IList<Guid> SiteIds { get; init; } = [];

    public string CostModel { get; set; } = "free";

    /// <summary>Dollars, as typed: "120" or "120.50".</summary>
    public string CostMin { get; set; } = string.Empty;

    public string CostMax { get; set; } = string.Empty;

    public string SlidingScaleNote { get; set; } = string.Empty;

    public string RegistrationMode { get; set; } = "external";

    public string ExternalUrl { get; set; } = string.Empty;

    public string Capacity { get; set; } = string.Empty;

    public bool MealsProvided { get; set; }

    public bool TransportProvided { get; set; }

    public string TransportNote { get; set; } = string.Empty;

    public bool IsDropIn { get; set; }

    public string AccessibilityNote { get; set; } = string.Empty;

    public string ContactEmail { get; set; } = string.Empty;

    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>"weekly" or "once".</summary>
    public string Repeats { get; set; } = "weekly";

    /// <summary>RFC 5545 day codes: MO, TU, WE, TH, FR, SA, SU.</summary>
    public IList<string> Days { get; init; } = [];

    /// <summary>yyyy-MM-dd, as an HTML date input sends it.</summary>
    public string StartsOn { get; set; } = string.Empty;

    public string EndsOn { get; set; } = string.Empty;

    /// <summary>HH:mm, as an HTML time input sends it.</summary>
    public string StartTime { get; set; } = string.Empty;

    public string EndTime { get; set; } = string.Empty;

    /// <summary>One of <c>ck_listing_deadline_kind</c>, or empty for none.</summary>
    public string DeadlineKind { get; set; } = string.Empty;

    public string DeadlineOn { get; set; } = string.Empty;

    /// <summary>
    /// The row version the editor was opened at. A save against a newer version is refused,
    /// so two people editing one listing cannot silently overwrite each other.
    /// </summary>
    public string Version { get; set; } = string.Empty;
}
