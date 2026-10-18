using System.Security.Claims;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Authorization;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Hosting.Auth;

/// <summary>
/// Builds the signed-in principal from the database: who the person is, what their roles let
/// them do, and which organisations they belong to.
/// </summary>
/// <remarks>
/// The one place a principal is made. Development sign-in calls it today and Identity sign-in
/// will call it later (D14); the cookie validator calls it again every few minutes, so a
/// revoked membership or a suspended account stops working without waiting for the cookie to
/// expire.
/// </remarks>
public sealed class BrightTimePrincipalFactory(AccountReader accounts, IDirectoryQueries directory)
{
    public const string AuthenticationType = "BrightTime";

    /// <summary>The security stamp the principal was built from.</summary>
    public const string StampClaim = "brighttime:stamp";

    /// <summary>The principal for an active account in this tenant, or null for anything else.</summary>
    public async Task<ClaimsPrincipal?> CreateAsync(
        TenantId tenant,
        UserId user,
        AuthenticationEvent authenticated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticated);

        AccountSnapshot? account = await accounts.GetAsync(tenant, user, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.IsActive || account.TenantId != tenant)
        {
            return null;
        }

        IReadOnlyList<OrganisationMembership> memberships =
            await directory.GetMembershipsAsync(user, cancellationToken).ConfigureAwait(false);

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, account.UserId.Value.ToString("D")),
            new(ClaimTypes.Name, account.UserName),
            new(BrightTimeClaims.TenantId, tenant.Value.ToString("D")),
            new(BrightTimeClaims.AccountKind, account.AccountKind),
            new(StampClaim, account.SecurityStamp),
            new(BrightTimeClaims.AuthTime, authenticated.At.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(BrightTimeClaims.AuthMethod, authenticated.Method),
            .. account.Roles.Select(r => new Claim(ClaimTypes.Role, r)),
            .. account.Permissions.Select(p => new Claim(BrightTimeClaims.Permission, p)),
            .. memberships.Select(m => new Claim(
                BrightTimeClaims.OrganisationMembership,
                BrightTimeClaims.FormatMembership(m.OrganisationId.Value, m.OrgRole))),
        ];

        if (account.Locale is { } locale)
        {
            claims.Add(new Claim(BrightTimeClaims.Locale, locale));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }
}

/// <summary>
/// When and how someone proved who they are. A fresh sign-in or a step-up makes a new one; a
/// cookie revalidation carries the old one forward, so refreshing a session never counts as
/// authenticating again.
/// </summary>
public sealed record AuthenticationEvent(string Method, DateTimeOffset At)
{
    /// <summary>The event a principal carries, or one so old it satisfies no step-up.</summary>
    public static AuthenticationEvent From(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new AuthenticationEvent(
            principal.GetAuthenticationMethod() ?? "unknown",
            principal.GetAuthenticatedAt() ?? DateTimeOffset.UnixEpoch);
    }
}
