using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>
/// Where a listing happens, as a family may see it. A private address carries no street
/// fields at all, only the neighbourhood; the family learns the address after registering.
/// </summary>
public sealed record ListingPlace(
    string Name,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? NeighbourhoodName,
    string? TransitNote,
    bool? IsWheelchairAccessible);

/// <summary>One upcoming session.</summary>
public sealed record ListingTime(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>A deadline a family should not miss.</summary>
public sealed record ListingDeadlineView(string Kind, DateTimeOffset DueAt, string? Label);

/// <summary>Everything the public listing page shows. Only ever built for a published, unexpired listing.</summary>
public sealed record ListingDetail
{
    public required ListingId Id { get; init; }

    public required string Slug { get; init; }

    public required ListingKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// The language of <see cref="Title"/> and <see cref="Summary"/>: the visitor's when a
    /// translation exists, otherwise the listing's own. The page marks it with <c>lang</c>.
    /// </summary>
    public string ContentLanguage { get; init; } = "en";

    /// <summary>The language of <see cref="Description"/>, which a translation may lack.</summary>
    public string DescriptionLanguage { get; init; } = "en";

    /// <summary>The listing's own language, which the provider wrote in.</summary>
    public string PrimaryLanguage { get; init; } = "en";

    /// <summary>True when the text shown is an unreviewed machine translation; the page says so.</summary>
    public bool IsMachineTranslated { get; init; }

    public required string OrganisationName { get; init; }

    public int? AgeMin { get; init; }

    public int? AgeMax { get; init; }

    public int? GradeMin { get; init; }

    public int? GradeMax { get; init; }

    public required CostModel CostModel { get; init; }

    public int? CostCentsMin { get; init; }

    public int? CostCentsMax { get; init; }

    public string? SlidingScaleNote { get; init; }

    public required Modality Modality { get; init; }

    public required RegistrationMode RegistrationMode { get; init; }

    /// <summary>Only an absolute http(s) URL, validated on write. Null when the link checker found it broken or blocked.</summary>
    public string? ExternalUrl { get; init; }

    public bool MealsProvided { get; init; }

    public bool TransportProvided { get; init; }

    public string? TransportNote { get; init; }

    public bool IsDropIn { get; init; }

    public string? AccessibilityNote { get; init; }

    public string? ContactEmail { get; init; }

    public string? ContactPhone { get; init; }

    public IReadOnlyList<string> Categories { get; init; } = [];

    public IReadOnlyList<string> Languages { get; init; } = [];

    public IReadOnlyList<string> Accommodations { get; init; } = [];

    public IReadOnlyList<ListingPlace> Places { get; init; } = [];

    public IReadOnlyList<ListingTime> Upcoming { get; init; } = [];

    public IReadOnlyList<ListingDeadlineView> Deadlines { get; init; } = [];
}

/// <summary>
/// Reads one published listing for the public page. Anonymous and tenant-scoped. Returns
/// null for anything a family must not see: a draft, an unpublished or expired listing, or
/// a slug from another tenant.
/// </summary>
public interface IListingDetails
{
    Task<ListingDetail?> GetPublishedAsync(
        TenantId tenant,
        string slug,
        string locale,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One listing in any state, as a family would see it once published: for an organiser's
    /// review and a provider's preview. Never for an anonymous page; the caller authorises.
    /// </summary>
    Task<ListingDetail?> GetForReviewAsync(
        TenantId tenant,
        Guid listingId,
        string locale,
        CancellationToken cancellationToken = default);
}
