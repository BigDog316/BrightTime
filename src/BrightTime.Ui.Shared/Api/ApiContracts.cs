using System.Text.Json;

namespace BrightTime.Ui.Shared.Api;

// The API's shapes as the app reads them (docs/plan/05). Written out here, not shared with the
// server, because the server's types live in its host; the API changes additively, so extra
// fields are ignored and every record tolerates what it does not know. ApiContractTests checks
// these against the running API.

/// <summary>A sentence to render from the catalogue: a key and its arguments (D30's "why").</summary>
public sealed record Phrase(string Key, IReadOnlyDictionary<string, JsonElement>? Args);

/// <summary>A page of results. Pass <see cref="NextCursor"/> back as <c>cursor</c> for the next one.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor, IReadOnlyList<Phrase>? Notices);

public sealed record OrganisationRef(Guid Id, string Slug, string Name);

public sealed record Cost(bool IsFree, int? FromCents);

public sealed record ListingSummary(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    OrganisationRef? Organisation,
    Cost Cost,
    DateTimeOffset? NextStartAt,
    DateTimeOffset? NextDeadlineAt,
    double? DistanceMetres,
    IReadOnlyList<Phrase> Why);

public sealed record Place(
    string Name,
    bool IsAddressPublic,
    string? AddressLine1,
    string? AddressLine2,
    string? Locality,
    string? PostalCode,
    string? Neighbourhood,
    string? TransitNote,
    bool? IsWheelchairAccessible);

public sealed record Session(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record Deadline(string Kind, DateTimeOffset DueAt, string? Label);

public sealed record ListingDetail(
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
    IReadOnlyList<Place> Places,
    IReadOnlyList<Session> Upcoming,
    IReadOnlyList<Deadline> Deadlines);

public sealed record Coordinate(double Longitude, double Latitude);

public sealed record NeighbourhoodSummary(string Code, string Name, int ListingCount, Coordinate Centre);

public sealed record Term(string Code, string Name, string? ParentCode, int SortOrder);

public sealed record LocaleInfo(string Code, string NativeName, string Direction);

/// <summary>The region the app serves: its name, languages and time zone (<c>GET /public/tenant</c>).</summary>
public sealed record TenantInfo(string Name, string DefaultLocale, string TimeZone, IReadOnlyList<LocaleInfo> Locales);

public sealed record TokenPair(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt)
{
    /// <summary>Never the tokens: a logged pair must not carry them.</summary>
    public override string ToString() => $"TokenPair {{ ExpiresIn = {ExpiresIn} }}";
}

public sealed record MeOrganisation(Guid OrganisationId, string Role);

/// <summary>The signed-in person: who, what kind of account (<c>adult</c>, <c>youth</c>), and what they may do.</summary>
public sealed record Me(
    Guid Id,
    string UserName,
    string AccountKind,
    string? Locale,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<MeOrganisation> Organisations)
{
    public bool IsYouth => AccountKind == "youth";

    public bool IsAdult => AccountKind == "adult";
}

/// <summary>
/// What the account may do (<c>GET /me/capabilities</c>): the COPPA gate, decided on the server.
/// The app shows and hides by this, never by its own reading of age or consent.
/// </summary>
public sealed record Capabilities(
    bool IsUnder13,
    bool HasAccountConsent,
    bool CanSelfEnroll,
    bool CanPostInCommunity,
    bool CanUploadMedia,
    bool CanPublishShowcase,
    bool CanReceiveSms,
    bool CanBeMessagedByOrganisation);

public sealed record SavedItem(ListingSummary Listing, string? Note, DateTimeOffset SavedAt);

public sealed record Interest(string Code, string Name, int Weight);

public sealed record SearchPreferences(string? NearNeighbourhood, int? MaxDistanceMetres);

public sealed record Reminder(Guid Id, Guid ListingId, string Anchor, int LeadMinutes, DateTimeOffset RemindAt, bool Sent);

public sealed record InboxItem(Guid Id, string Kind, string Title, string Body, string? DeepLink, DateTimeOffset CreatedAt, bool IsRead);

public sealed record ChannelChoice(string Kind, string Channel, bool Enabled);

public sealed record NotificationSettings(string? QuietFrom, string? QuietTo, IReadOnlyList<ChannelChoice> Choices);

public sealed record GuardianLink(Guid Id, string Relationship, string Status, string? VerificationMethod, DateTimeOffset? VerifiedAt);

public sealed record GivenConsent(string Scope, string Method, DateTimeOffset GrantedAt);

/// <summary>A guardian's child, or a young person they still consent for once it is claimed (<c>accountKind: youth</c>).</summary>
public sealed record Child(
    Guid Id,
    string GivenName,
    string BirthMonth,
    string AccountKind,
    string Status,
    GuardianLink Link,
    IReadOnlyList<GivenConsent> Consents,
    Capabilities Capabilities)
{
    public bool IsClaimed => AccountKind == "youth";
}

/// <summary>What a consent scope means, in the exact words a grant is recorded against (its <see cref="Hash"/>).</summary>
public sealed record Disclosure(string Scope, string Version, string Text, string Hash, IReadOnlyList<string> Methods, bool AvailableUnder13);

/// <summary>An RFC 9457 problem from the API, or one the app makes for a request that never arrived.</summary>
public sealed record ApiProblem(string Type, string? Title, int Status, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public const string Prefix = "urn:brighttime:problem:";

    /// <summary>The app could not reach the server.</summary>
    public const string OfflineType = "urn:brighttime:app:offline";

    /// <summary>The server answered with something the app could not read.</summary>
    public const string UnreadableType = "urn:brighttime:app:unreadable";

    /// <summary>The error code after the prefix (<c>consent.token_invalid</c>), or null for another kind of problem.</summary>
    public string? Code => Type.StartsWith(Prefix, StringComparison.Ordinal) ? Type[Prefix.Length..] : null;

    public static ApiProblem Offline { get; } = new(OfflineType, null, 0);
}
