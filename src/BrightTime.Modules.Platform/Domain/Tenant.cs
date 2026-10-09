using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Platform.Domain;

/// <summary>A region deployment: branding, locales, geography, feature flags and its compliance pack.</summary>
public sealed class Tenant
{
    public Guid Id { get; init; }

    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Human-readable region, e.g. "Kettle County, New York".</summary>
    public required string RegionName { get; set; }

    public required string RegionKind { get; set; }

    public required string DefaultLocale { get; set; }

    /// <summary>IANA timezone. Wall-clock questions ("due today") are answered in this zone, not UTC.</summary>
    public required string Timezone { get; set; }

    public string Status { get; set; } = TenantStatus.Provisioning;

    public string Branding { get; set; } = "{}";

    /// <summary>
    /// Selects the student-privacy policy pack: consent copy, retention
    /// defaults, breach-notification SLA and whether a DPA is required before a
    /// district connection can be enabled.
    /// </summary>
    public string StudentPrivacyPack { get; set; } = Domain.StudentPrivacyPack.FerpaBaseline;

    /// <summary>
    /// The suppression floor for aggregates. Default 10, hard minimum 5
    /// (<c>ck_tenant_min_cell</c>). In a county of eighteen neighbourhoods, a
    /// cell of three re-identifies.
    /// </summary>
    public short MinCellSize { get; set; } = 10;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public TenantId TenantId => new(Id);

    public bool IsActive => Status == TenantStatus.Active;

    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(Timezone);
}

public static class TenantStatus
{
    public const string Provisioning = "provisioning";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Archived = "archived";
}

public static class StudentPrivacyPack
{
    public const string FerpaBaseline = "us-ferpa-baseline";

    /// <summary>
    /// New York Education Law 2-d and Part 121: parents' bill of rights at every
    /// consent point, encryption in transit and at rest, 7-day breach
    /// notification, DPA before any district connection, annual training
    /// records, and no commercial use of student data.
    /// </summary>
    public const string NewYorkEdLaw2D = "ny-ed-law-2d";

    public const string CaliforniaSopipa = "ca-sopipa";
}
