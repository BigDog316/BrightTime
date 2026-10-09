using BrightTime.Modules.Platform.Domain;
using BrightTime.Modules.Platform.Tenancy;

namespace BrightTime.Modules.Platform.Tests;

public sealed class TenantResolverTests
{
    [Theory]
    [InlineData("Kettle.BrightTime.org", "kettle.brighttime.org")]
    [InlineData("kettle.brighttime.org:8443", "kettle.brighttime.org")]
    [InlineData("  KETTLE.BRIGHTTIME.ORG  ", "kettle.brighttime.org")]
    [InlineData("kettle.brighttime.org.", "kettle.brighttime.org")]
    [InlineData("localhost:5001", "localhost")]
    public void Host_normalisation_matches_the_citext_column(string host, string expected) =>
        TenantResolver.NormaliseHost(host).ShouldBe(expected);

    [Fact]
    public void A_tenant_defaults_to_the_ferpa_baseline_pack()
    {
        // A new tenant must not start with weaker obligations than the floor.
        // Stronger packs (ny-ed-law-2d) are opted into per region.
        var tenant = new Tenant
        {
            Slug = "kettle",
            DisplayName = "BrightTime Kettle County",
            RegionName = "Kettle County, New York",
            RegionKind = "county",
            DefaultLocale = "en",
            Timezone = "America/New_York",
        };

        tenant.StudentPrivacyPack.ShouldBe(StudentPrivacyPack.FerpaBaseline);
    }

    [Fact]
    public void A_tenant_defaults_to_a_suppression_floor_of_ten()
    {
        // In a county of eighteen neighbourhoods, a cell of three
        // re-identifies. ck_tenant_min_cell hard-floors this at 5.
        var tenant = new Tenant
        {
            Slug = "kettle",
            DisplayName = "BrightTime Kettle County",
            RegionName = "Kettle County, New York",
            RegionKind = "county",
            DefaultLocale = "en",
            Timezone = "America/New_York",
        };

        tenant.MinCellSize.ShouldBe((short)10);
    }

    [Fact]
    public void A_tenant_is_not_active_until_provisioning_completes()
    {
        var tenant = new Tenant
        {
            Slug = "kettle",
            DisplayName = "BrightTime Kettle County",
            RegionName = "Kettle County, New York",
            RegionKind = "county",
            DefaultLocale = "en",
            Timezone = "America/New_York",
        };

        tenant.Status.ShouldBe(TenantStatus.Provisioning);
        tenant.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void The_region_timezone_resolves()
    {
        var tenant = new Tenant
        {
            Slug = "kettle",
            DisplayName = "BrightTime Kettle County",
            RegionName = "Kettle County, New York",
            RegionKind = "county",
            DefaultLocale = "en",
            Timezone = "America/New_York",
        };

        tenant.ResolveTimeZone().Id.ShouldBe("America/New_York");
    }
}
