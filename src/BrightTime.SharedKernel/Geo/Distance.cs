namespace BrightTime.SharedKernel.Geo;

/// <summary>
/// A distance in metres, with the search radius bounds the API accepts.
/// </summary>
public readonly record struct Distance(int Metres) : IComparable<Distance>
{
    /// <summary>Below this, a radius filter is narrower than typical geocoding error.</summary>
    public const int MinSearchRadiusMetres = 100;

    /// <summary>About 50 miles: beyond a county, a distance filter stops meaning anything.</summary>
    public const int MaxSearchRadiusMetres = 80_000;

    public static Distance FromMetres(int metres) => new(metres);

    public static Distance FromKilometres(double kilometres) => new((int)Math.Round(kilometres * 1000));

    public double Kilometres => Metres / 1000.0;

    public double Miles => Metres / 1609.344;

    public static Distance ClampToSearchRadius(int metres) =>
        new(Math.Clamp(metres, MinSearchRadiusMetres, MaxSearchRadiusMetres));

    public int CompareTo(Distance other) => Metres.CompareTo(other.Metres);

    public static bool operator <(Distance left, Distance right) => left.Metres < right.Metres;

    public static bool operator >(Distance left, Distance right) => left.Metres > right.Metres;

    public static bool operator <=(Distance left, Distance right) => left.Metres <= right.Metres;

    public static bool operator >=(Distance left, Distance right) => left.Metres >= right.Metres;

    public override string ToString() =>
        Metres < 1000
            ? $"{Metres} m"
            : Kilometres.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km";
}
