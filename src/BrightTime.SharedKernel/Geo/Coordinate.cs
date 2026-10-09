using System.Globalization;

namespace BrightTime.SharedKernel.Geo;

/// <summary>
/// A WGS 84 point. Validated on construction, because an unvalidated
/// latitude/longitude pair silently becomes a point in the Gulf of Guinea and
/// every distance search quietly returns nothing.
/// </summary>
public readonly record struct Coordinate
{
    public const int Srid = 4326;

    public Coordinate(double latitude, double longitude)
    {
        if (double.IsNaN(latitude) || latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90.");
        }

        if (double.IsNaN(longitude) || longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180.");
        }

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }

    public double Longitude { get; }

    public static bool TryCreate(double latitude, double longitude, out Coordinate coordinate)
    {
        if (double.IsNaN(latitude) || double.IsNaN(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            coordinate = default;
            return false;
        }

        coordinate = new Coordinate(latitude, longitude);
        return true;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Latitude:0.######},{Longitude:0.######}");
}
