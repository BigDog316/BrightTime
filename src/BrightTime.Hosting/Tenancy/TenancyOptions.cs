namespace BrightTime.Hosting.Tenancy;

/// <summary>How a request finds its tenant.</summary>
public enum TenancyMode
{
    /// <summary>From the request's host, via <c>platform.tenant_domain</c>. The default with a database.</summary>
    Host,

    /// <summary>One configured tenant for every request: a single-tenant install, or development.</summary>
    Fixed,
}

public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    public TenancyMode Mode { get; set; } = TenancyMode.Host;

    /// <summary>The tenant every request belongs to in <see cref="TenancyMode.Fixed"/>.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The scheme of tenants' own addresses, built from their primary domain: https everywhere
    /// but local development.
    /// </summary>
    public string PublicScheme { get; set; } = "https";

    /// <summary>A port for tenants' addresses, for local development only (kettle.localhost:5176).</summary>
    public int? PublicPort { get; set; }
}
