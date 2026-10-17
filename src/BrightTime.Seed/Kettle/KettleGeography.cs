namespace BrightTime.Seed.Kettle;

/// <summary>
/// The fictional county's map: one county, 11 municipalities, 18 neighbourhoods, 4 school
/// districts, 28 schools and one bus agency.
/// </summary>
/// <remarks>
/// Neighbourhoods are cells of a 6 x 3 grid laid over the county's box, filled row by row.
/// Municipality, county and district boundaries are the unions of their cells, computed
/// by PostGIS when the seed is written. The shapes are deliberately plain: they have to be
/// valid polygons that contain the right points, not look like a real place.
/// </remarks>
internal static class KettleGeography
{
    public const string CountyCode = "kettle-county";

    /// <summary>The county's extent. Any coordinate is somewhere real; nothing here refers to it.</summary>
    public static readonly Box County = new(-73.86, 40.98, -73.62, 41.14);

    private const int Columns = 6;
    private const int Rows = 3;

    /// <summary>Municipality name, then its neighbourhoods in grid order.</summary>
    private static readonly (string Municipality, string[] Neighbourhoods)[] Layout =
    [
        ("Kettleton", ["Downtown", "Westside", "Riverside", "Northgate", "Eastbank"]),
        ("Grayhaven", ["Grayhaven Village", "Grayhaven Heights"]),
        ("Brannock", ["Brannock Center", "Mill District"]),
        ("Cedar Run", ["Cedar Run", "Lakeview"]),
        ("Alder Crossing", ["Alder Crossing"]),
        ("Fenwick Hollow", ["Fenwick Hollow"]),
        ("Hollis Bend", ["Hollis Bend"]),
        ("Marrow Point", ["Marrow Point"]),
        ("Orrin Valley", ["Orrin Valley"]),
        ("Quarry Hill", ["Quarry Hill"]),
        ("Wexley", ["Wexley"]),
    ];

    /// <summary>District name, then the municipalities it serves.</summary>
    private static readonly (string District, string[] Municipalities)[] DistrictLayout =
    [
        ("Kettleton City School District", ["Kettleton"]),
        ("Grayhaven-Brannock Central School District", ["Grayhaven", "Brannock", "Cedar Run"]),
        ("Alder Valley Central School District", ["Alder Crossing", "Fenwick Hollow", "Hollis Bend"]),
        ("Quarry Hill-Wexley Union Free School District", ["Marrow Point", "Orrin Valley", "Quarry Hill", "Wexley"]),
    ];

    /// <summary>Postal codes for the seed's addresses. Fictional, like the rest.</summary>
    public static string PostalCodeFor(string municipality) =>
        "1" + (2900 + (Array.FindIndex(Layout, l => l.Municipality == municipality) * 7)).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Municipalities { get; } = [.. Layout.Select(l => l.Municipality)];

    public static Guid AreaId(string code) => SeedIds.For("area/" + code);

    public static string Code(string name) => SharedKernel.Primitives.Slug.From(name);

    public static IReadOnlyList<SeedArea> Areas()
    {
        Guid county = AreaId(CountyCode);
        List<SeedArea> areas = [new(county, null, CountyCode, "county", "Kettle County", County)];

        double width = (County.East - County.West) / Columns;
        double height = (County.North - County.South) / Rows;
        int cell = 0;

        foreach ((string municipality, string[] neighbourhoods) in Layout)
        {
            string municipalityCode = "town-" + Code(municipality);
            Guid municipalityId = AreaId(municipalityCode);
            areas.Add(new SeedArea(municipalityId, county, municipalityCode, "municipality", municipality, null));

            foreach (string name in neighbourhoods)
            {
                int column = cell % Columns;
                int row = cell / Columns;
                double north = County.North - (row * height);
                Box box = new(County.West + (column * width), north - height, County.West + ((column + 1) * width), north);

                // A one-neighbourhood town reuses its own name, so the code says which is which.
                string code = "hood-" + Code(name);
                areas.Add(new SeedArea(AreaId(code), municipalityId, code, "neighbourhood", name, box));
                cell++;
            }
        }

        return areas;
    }

    public static IReadOnlyList<SeedArea> Neighbourhoods(IReadOnlyList<SeedArea> areas) =>
        [.. areas.Where(a => a.Kind == "neighbourhood")];

    /// <summary>The neighbourhoods of one municipality.</summary>
    public static IReadOnlyList<SeedArea> NeighbourhoodsOf(IReadOnlyList<SeedArea> areas, string municipality)
    {
        Guid id = AreaId("town-" + Code(municipality));
        return [.. areas.Where(a => a.ParentId == id)];
    }

    public static string MunicipalityOf(IReadOnlyList<SeedArea> areas, Guid neighbourhoodId)
    {
        SeedArea hood = areas.Single(a => a.Id == neighbourhoodId);
        return areas.Single(a => a.Id == hood.ParentId).Name;
    }

    public static IReadOnlyList<SeedDistrict> Districts() =>
    [
        .. DistrictLayout.Select(d => new SeedDistrict(
            SeedIds.For("district/" + d.District),
            d.District,
            [.. d.Municipalities.Select(m => AreaId("town-" + Code(m)))])),
    ];

    /// <summary>
    /// 28 schools: Kettleton has ten (six elementary, two middle, two high), each other
    /// district six (three elementary, one middle, one high, one K-8).
    /// </summary>
    public static IReadOnlyList<SeedSchool> Schools(IReadOnlyList<SeedArea> areas, IReadOnlyList<SeedDistrict> districts)
    {
        List<SeedSchool> schools = [];

        for (int d = 0; d < DistrictLayout.Length; d++)
        {
            (string district, string[] municipalities) = DistrictLayout[d];
            SeedArea[] hoods = [.. municipalities.SelectMany(m => NeighbourhoodsOf(areas, m))];

            (string Suffix, short Min, short Max)[] plan = d == 0
                ? [("Elementary School", 0, 5), ("Elementary School", 0, 5), ("Elementary School", 0, 5),
                   ("Elementary School", 0, 5), ("Elementary School", 0, 5), ("Elementary School", 0, 5),
                   ("Middle School", 6, 8), ("Middle School", 6, 8), ("High School", 9, 12), ("High School", 9, 12)]
                : [("Elementary School", 0, 5), ("Elementary School", 0, 5), ("Elementary School", 0, 5),
                   ("Middle School", 6, 8), ("High School", 9, 12), ("K-8 School", 0, 8)];

            for (int i = 0; i < plan.Length; i++)
            {
                SeedArea hood = hoods[i % hoods.Length];
                (string suffix, short min, short max) = plan[i];

                // Two schools of a kind in one neighbourhood get a distinguishing name.
                string baseName = hood.Name;
                int sameBefore = Enumerable.Range(0, i).Count(j => hoods[j % hoods.Length] == hood && plan[j].Suffix == suffix);
                string name = sameBefore == 0 ? $"{baseName} {suffix}" : $"{baseName} {Ordinal(sameBefore)} {suffix}";

                (double lon, double lat) = hood.Box!.Value.PointAt(Fraction($"school/{name}/x"), Fraction($"school/{name}/y"));
                schools.Add(new SeedSchool(SeedIds.For("school/" + name), districts[d].Id, name, min, max, lon, lat, hood.Id));
            }
        }

        return schools;
    }

    /// <summary>One bus agency with a stop at the middle of every neighbourhood.</summary>
    public static SeedTransit Transit(IReadOnlyList<SeedArea> areas)
    {
        IReadOnlyList<SeedArea> hoods = Neighbourhoods(areas);
        List<SeedTransitStop> stops = [];

        for (int i = 0; i < hoods.Count; i++)
        {
            (double lon, double lat) = hoods[i].Box!.Value.Centre;
            string gtfs = "KCT" + (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            stops.Add(new SeedTransitStop(SeedIds.For("stop/" + gtfs), gtfs, $"{hoods[i].Name} Transit Center", lon, lat, i % 5 != 4));
        }

        return new SeedTransit(
            SeedIds.For("transit/kct"),
            "Kettle County Transit",
            "https://example.org/kettle-county-transit/gtfs.zip",
            stops);
    }

    /// <summary>The bus route that serves a neighbourhood, for site transit notes.</summary>
    public static int RouteFor(IReadOnlyList<SeedArea> areas, Guid neighbourhoodId)
    {
        IReadOnlyList<SeedArea> hoods = Neighbourhoods(areas);
        int index = hoods.ToList().FindIndex(h => h.Id == neighbourhoodId);
        return 1 + (index % 9);
    }

    /// <summary>A stable fraction in [0, 1) derived from a name.</summary>
    public static double Fraction(string name) => (SeedIds.Hash(name) % 10_000) / 10_000.0;

    private static string Ordinal(int n) => n switch
    {
        1 => "North",
        2 => "South",
        _ => "East",
    };
}
