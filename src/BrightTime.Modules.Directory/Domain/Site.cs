using NetTopologySuite.Geometries;

namespace BrightTime.Modules.Directory.Domain;

/// <summary>
/// A place a program happens.
/// </summary>
/// <remarks>
/// <see cref="IsAddressPublic"/> is the one to watch. A site hosted in
/// someone's home, or a shelter-based program, needs to be findable by
/// distance without publishing its street address — so the location is always
/// geocoded for search, and the API serialiser emits only the neighbourhood
/// when the flag is false. <c>ck_site_private_needs_hood</c> makes a private
/// address without a neighbourhood a constraint violation, because otherwise
/// such a site would be invisible rather than merely discreet.
/// </remarks>
public sealed class Site
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid OrgId { get; init; }

    public required string Name { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? Locality { get; set; }

    public string? RegionCode { get; set; }

    public string? PostalCode { get; set; }

    public string CountryCode { get; set; } = "US";

    public Point? Location { get; set; }

    public string? GeocodeAccuracy { get; set; }

    public Guid? NeighbourhoodId { get; set; }

    public Guid? NearestSchoolId { get; set; }

    public bool IsAddressPublic { get; set; } = true;

    public string? EntranceNote { get; set; }

    public string? TransitNote { get; set; }

    public string? ParkingNote { get; set; }

    public bool? IsWheelchairAccessible { get; set; }

    public bool? HasAccessibleRestroom { get; set; }

    public bool? HasElevator { get; set; }

    public bool? HasQuietSpace { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class GeocodeAccuracyValues
{
    public const string Rooftop = "rooftop";
    public const string Parcel = "parcel";
    public const string Interpolated = "interpolated";
    public const string Street = "street";
    public const string Locality = "locality";
    public const string Manual = "manual";

    /// <summary>
    /// Accuracy good enough for a distance filter. A locality-level geocode
    /// puts every site in a town on the same pin, which makes "within 2 km"
    /// a lie.
    /// </summary>
    public static bool IsUsableForDistanceSearch(string? accuracy) =>
        accuracy is Rooftop or Parcel or Interpolated or Street or Manual;
}
