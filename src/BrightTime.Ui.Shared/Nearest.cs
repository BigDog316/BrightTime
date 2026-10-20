using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Ui.Shared;

/// <summary>Distances worked out on the device, so a position is never sent anywhere.</summary>
public static class Nearest
{
    private const double EarthRadiusMetres = 6_371_000;

    /// <summary>The neighbourhood whose centre is closest to <paramref name="here"/>.</summary>
    public static NeighbourhoodSummary Neighbourhood(IReadOnlyList<NeighbourhoodSummary> neighbourhoods, GeoPoint here)
    {
        ArgumentNullException.ThrowIfNull(neighbourhoods);
        if (neighbourhoods.Count == 0)
        {
            throw new ArgumentException("There are no neighbourhoods to choose from.", nameof(neighbourhoods));
        }

        return neighbourhoods.MinBy(n => Metres(here, new GeoPoint(n.Centre.Latitude, n.Centre.Longitude)))!;
    }

    /// <summary>Great-circle distance, by the haversine formula: plenty for "which centre is nearer".</summary>
    public static double Metres(GeoPoint a, GeoPoint b)
    {
        double dLat = Radians(b.Latitude - a.Latitude);
        double dLon = Radians(b.Longitude - a.Longitude);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                   + (Math.Cos(Radians(a.Latitude)) * Math.Cos(Radians(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
