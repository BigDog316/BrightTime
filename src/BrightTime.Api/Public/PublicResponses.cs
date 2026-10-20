using System.Text.Json;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Api.Public;

// The public API's response shapes. They are the contract the app and third parties build on,
// so they are written out here rather than serialising module types, and they change additively.

/// <summary>
/// A sentence the client renders from its own copy of the interface catalogues: a key and its
/// arguments, so it reads in the client's language and stays in step with the web's wording.
/// </summary>
public sealed record PhraseResponse(string Key, IReadOnlyDictionary<string, object> Args)
{
    public static PhraseResponse From(Phrase phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        return new(phrase.Key, phrase.Arguments);
    }
}

/// <summary>A page of results. <c>nextCursor</c> is opaque; pass it back as <c>cursor</c>.</summary>
public sealed record PageResponse<T>(IReadOnlyList<T> Items, string? NextCursor, IReadOnlyList<PhraseResponse> Notices);

public sealed record OrganisationRef(Guid Id, string Slug, string Name);

public sealed record CostResponse(bool IsFree, int? FromCents);

/// <summary>One search result. <c>why</c> is the explainable ranking: what made it a match.</summary>
public sealed record ListingSummaryResponse(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    OrganisationRef? Organisation,
    CostResponse Cost,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextDeadlineAt,
    double? DistanceMetres,
    IReadOnlyList<PhraseResponse> Why);

/// <summary>
/// Where a listing happens. The address is present only when the provider made it public; a
/// private site gives its name and neighbourhood only.
/// </summary>
public sealed record PlaceResponse(
    string Name,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? Neighbourhood,
    string? TransitNote,
    bool? IsWheelchairAccessible);

public sealed record SessionResponse(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record DeadlineResponse(string Kind, DateTimeOffset DueAt, string? Label);

public sealed record ListingDetailResponse(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    string? Description,
    string ContentLanguage,
    string DescriptionLanguage,
    bool IsMachineTranslated,
    OrganisationRef Organisation,
    int? AgeMin,
    int? AgeMax,
    int? GradeMin,
    int? GradeMax,
    string CostModel,
    int? CostCentsMin,
    int? CostCentsMax,
    string? SlidingScaleNote,
    string Modality,
    string RegistrationMode,
    string? ExternalUrl,
    bool MealsProvided,
    bool TransportProvided,
    string? TransportNote,
    bool IsDropIn,
    string? AccessibilityNote,
    string? ContactEmail,
    string? ContactPhone,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Accommodations,
    IReadOnlyList<PlaceResponse> Places,
    IReadOnlyList<SessionResponse> Upcoming,
    IReadOnlyList<DeadlineResponse> Deadlines);

public sealed record CollectionSummaryResponse(string Slug, string Title, string? Description, string ContentLanguage, int ListingCount);

public sealed record CollectionResponse(
    string Slug,
    string Title,
    string? Description,
    string ContentLanguage,
    IReadOnlyList<CollectionItemResponse> Items);

public sealed record CollectionItemResponse(ListingSummaryResponse Listing, string? CuratorNote);

public sealed record CoordinateResponse(double Longitude, double Latitude);

public sealed record NeighbourhoodSummaryResponse(string Code, string Name, int ListingCount, CoordinateResponse Centre);

/// <summary>
/// A neighbourhood's page. <c>boundary</c> is its rings as [longitude, latitude] pairs, filled
/// even-odd: public geography, the same areas the web's map draws (D21). No site's position is
/// in it.
/// </summary>
public sealed record NeighbourhoodResponse(
    string Code,
    string Name,
    int ListingCount,
    CoordinateResponse Centre,
    IReadOnlyList<IReadOnlyList<double[]>> Boundary,
    string? ContentLanguage,
    IReadOnlyList<PageBlockResponse> Blocks);

/// <summary>An organiser-written block. <c>body</c> is plain Markdown; render it with a sanitising renderer.</summary>
public sealed record PageBlockResponse(string Kind, string? Heading, string? Body, JsonElement Payload);

public sealed record SiteResponse(
    string Name,
    string? Neighbourhood,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? TransitNote,
    bool? IsWheelchairAccessible,
    bool? HasAccessibleRestroom,
    bool? HasElevator,
    bool? HasQuietSpace);

public sealed record OrganisationResponse(
    Guid Id,
    string Slug,
    string Name,
    string Kind,
    string? Mission,
    string? WebsiteUrl,
    string? PublicEmail,
    string? PublicPhone,
    IReadOnlyList<string> LanguagesServed,
    IReadOnlyList<SiteResponse> Sites);

public sealed record TermResponse(string Code, string Name, string? ParentCode, int SortOrder);

public sealed record LocaleResponse(string Code, string NativeName, string Direction);

public sealed record TenantResponse(string Name, string DefaultLocale, string TimeZone, IReadOnlyList<LocaleResponse> Locales);
