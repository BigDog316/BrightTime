namespace BrightTime.Modules.Identity.Authorization;

/// <summary>
/// The claim types BrightTime adds to a principal.
/// </summary>
/// <remarks>
/// Organisation memberships are claims rather than a per-request query because
/// they are read on nearly every authenticated request. They are also the
/// reason the security stamp matters: revoking a staff member has to invalidate
/// their existing cookie, not wait for it to expire.
/// </remarks>
public static class BrightTimeClaims
{
    public const string TenantId = "brighttime:tenant";

    public const string Permission = "brighttime:perm";

    /// <summary>One claim per membership, valued <c>{organisationId}:{orgRole}</c>.</summary>
    public const string OrganisationMembership = "brighttime:org";

    public const string AccountKind = "brighttime:account_kind";

    public const string AgeBand = "brighttime:age_band";

    public const string Locale = "brighttime:locale";

    public static string FormatMembership(Guid organisationId, string orgRole) =>
        $"{organisationId:D}:{orgRole}";

    public static bool TryParseMembership(string? value, out Guid organisationId, out string orgRole)
    {
        organisationId = Guid.Empty;
        orgRole = string.Empty;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        if (!Guid.TryParse(value.AsSpan(0, separator), out organisationId))
        {
            return false;
        }

        orgRole = value[(separator + 1)..];
        return true;
    }
}
