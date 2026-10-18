using BrightTime.Modules.Platform.Tenancy;

namespace BrightTime.Hosting.Tenancy;

/// <summary>
/// The request's tenant as a site: its record and its own address.
/// </summary>
/// <remarks>
/// <see cref="BaseUrl"/> comes from the tenant's primary domain in the database (or, for a
/// fixed tenant, from configuration), never from the request's Host header. Everything that
/// puts this site's address somewhere a person will follow it uses it: reset links, the
/// passkey relying party, calendar files.
/// </remarks>
public sealed class SiteContext
{
    public TenantSite? Site { get; private set; }

    public Uri? BaseUrl { get; private set; }

    public void Set(TenantSite? site, Uri? baseUrl)
    {
        Site = site;
        BaseUrl = baseUrl;
    }
}
