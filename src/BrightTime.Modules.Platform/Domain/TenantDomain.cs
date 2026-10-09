namespace BrightTime.Modules.Platform.Domain;

/// <summary>
/// Maps a host to a tenant.
/// </summary>
/// <remarks>
/// The one table readable before tenant context exists — resolution has to
/// start somewhere. It holds no personal data, and
/// <c>tenant_domain_lookup</c> in the RLS file is the policy that permits the
/// context-free read.
/// </remarks>
public sealed class TenantDomain
{
    public required string Host { get; init; }

    public Guid TenantId { get; init; }

    public bool IsPrimary { get; set; }
}
