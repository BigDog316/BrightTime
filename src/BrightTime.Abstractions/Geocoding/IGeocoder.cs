using BrightTime.SharedKernel.Geo;

namespace BrightTime.Abstractions.Geocoding;

/// <summary>How precisely an address resolved. Anything looser than <see cref="Street"/> is not good enough for a distance filter.</summary>
public enum GeocodeAccuracy
{
    Unknown,
    Locality,
    Street,
    Interpolated,
    Parcel,
    Rooftop,
}

public sealed record GeocodeResult(Coordinate Coordinate, GeocodeAccuracy Accuracy, string? FormattedAddress);

/// <summary>
/// Address to coordinate.
/// </summary>
/// <remarks>
/// Two rules, both from docs/plan/08-decisions.md D8. A family's typed address
/// is geocoded in-request and never persisted, never logged and never cached —
/// <see cref="GeocodeTransientAsync"/> exists to make that call site obvious.
/// Provider sites and schools are public locations and may be cached, which is
/// what <see cref="GeocodeCacheableAsync"/> is for.
/// </remarks>
public interface IGeocoder
{
    /// <summary>Geocodes a value that must not be retained anywhere. Implementations must not cache, log or persist the query.</summary>
    Task<GeocodeResult?> GeocodeTransientAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Geocodes a public location, which may be cached.</summary>
    Task<GeocodeResult?> GeocodeCacheableAsync(string query, CancellationToken cancellationToken = default);
}
