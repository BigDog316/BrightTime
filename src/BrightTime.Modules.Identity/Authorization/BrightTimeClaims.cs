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

    /// <summary>
    /// When the person last proved who they are, in Unix seconds (the OpenID Connect claim
    /// name). Set at sign-in and at step-up; kept, not reset, when the cookie is revalidated.
    /// </summary>
    public const string AuthTime = "auth_time";

    /// <summary>How they proved it: one of <see cref="AuthenticationMethods"/>.</summary>
    public const string AuthMethod = "brighttime:amr";

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
