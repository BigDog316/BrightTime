using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Localisation;

/// <summary>
/// Whose words, in which language and time zone: what <see cref="Text"/> needs to know about
/// the reader. Read on every string, so a locale settled after <see cref="Text"/> was made
/// still applies.
/// </summary>
/// <remarks>
/// On a server it is the request's tenant (BrightTime.Hosting adapts the tenant context). The
/// app supplies its own region and the person's language setting.
/// </remarks>
public interface ITextContext
{
    /// <summary>The tenant whose wording overrides apply; <see cref="TenantId.None"/> for none.</summary>
    TenantId TenantId { get; }

    bool IsResolved { get; }

    Locale Locale { get; }

    /// <summary>Dates and times are written in this zone: the region's, not the server's.</summary>
    TimeZoneInfo TimeZone { get; }
}
