namespace BrightTime.Infrastructure.Tenancy;

/// <summary>
/// The current tenant's own address, for work with no request behind it: the worker signing a
/// badge (D91) names the site in the credential, as a request host takes from its site context.
/// </summary>
public interface ISiteAddress
{
    /// <summary>The tenant's address, from its primary domain (or a single-site install's fixed one), ending in <c>/</c>.</summary>
    Task<Uri> BaseUrlAsync(CancellationToken cancellationToken = default);
}
