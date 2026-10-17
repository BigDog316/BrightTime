namespace BrightTime.Seed.Kettle;

/// <summary>
/// The whole Kettle County catalogue for a given day. Pure: no database, no clock.
/// </summary>
internal static class KettleSeed
{
    /// <summary>The same tenant the sample finder uses, so one configuration serves both modes.</summary>
    public static readonly Guid TenantId = BrightTime.Modules.Opportunities.Search.Sample.KettleCatalogue.TenantId;

    public const string TenantSlug = "kettle";

    public const string DisplayName = "BrightTime Kettle County";

    public const string RegionName = "Kettle County, New York";

    /// <summary>Hosts that will resolve to the tenant once host-based resolution exists.</summary>
    public static readonly string[] Hosts = ["kettle.localhost", "kettle.brighttime.test"];

    public static SeedData Build(DateOnly today)
    {
        IReadOnlyList<SeedArea> areas = KettleGeography.Areas();
        IReadOnlyList<SeedDistrict> districts = KettleGeography.Districts();
        DateTimeOffset now = KettleListings.At(
            TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone), today, new TimeOnly(12, 0));

        IReadOnlyList<SeedOrganisation> organisations = KettleOrganisations.Organisations(now);
        IReadOnlyList<SeedSite> sites = KettleOrganisations.Sites(areas);

        return new SeedData(
            TenantId,
            areas,
            districts,
            KettleGeography.Schools(areas, districts),
            KettleGeography.Transit(areas),
            KettleTaxonomy.Terms(),
            organisations,
            sites,
            KettleListings.Build(organisations, sites, areas, today),
            KettleAccounts.Roles(),
            KettleAccounts.Users(organisations, sites));
    }
}
