using System.Globalization;
using System.Security.Claims;
using BrightTime.Hosting.Auth;
using BrightTime.Modules.Identity.Authorization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BrightTime.Api.Auth;

/// <summary>
/// Short-lived access tokens (JWT, HS256), carrying who and when and nothing they could be
/// trusted for on their own.
/// </summary>
/// <remarks>
/// <para>
/// A token names its subject, its tenant, the account's security stamp, and when and how the
/// person signed in. Roles, permissions and memberships are not in it: every request rebuilds
/// them from the database (<see cref="BrightTimePrincipalFactory"/>) and refuses the token if
/// the stamp has changed. So a revoked membership, a suspended account or "sign out everywhere"
/// takes effect at the next request, not when the token expires.
/// </para>
/// <para>
/// Symmetric signing, because the API both issues and verifies and nothing else needs to. The
/// issuer is the tenant's own address, so a token from one tenant is refused by another even
/// before the tenant claim is compared.
/// </para>
/// </remarks>
public sealed class AccessTokens(TokenOptions options, IReadOnlyList<(string Id, byte[] Key)> keys)
{
    public const string TenantClaim = "tid";
    public const string StampClaim = "stamp";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>Every key, for verifying.</summary>
    public IEnumerable<SecurityKey> VerificationKeys => keys.Select(k => new SymmetricSecurityKey(k.Key) { KeyId = k.Id });

    /// <summary>A token for <paramref name="principal"/>, issued by the tenant at <paramref name="issuer"/>.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(ClaimsPrincipal principal, Uri issuer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(issuer);

        (string id, byte[] key) = keys[0];
        DateTimeOffset expires = now + options.AccessLifetime;
        AuthenticationEvent authenticated = AuthenticationEvent.From(principal);

        string token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer.AbsoluteUri,
            Audience = TokenOptions.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key) { KeyId = id }, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [JwtRegisteredClaimNames.Sub] = principal.FindFirstValue(ClaimTypes.NameIdentifier)!,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [TenantClaim] = principal.FindFirstValue(BrightTimeClaims.TenantId)!,
                [StampClaim] = principal.FindFirstValue(BrightTimePrincipalFactory.StampClaim)!,
                [JwtRegisteredClaimNames.AuthTime] = authenticated.At.ToUnixTimeSeconds(),
                [JwtRegisteredClaimNames.Amr] = authenticated.Method,
            },
        });

        return (token, expires);
    }

    /// <summary>How a validated token's claims are read back.</summary>
    public static (Guid User, Guid Tenant, string Stamp, AuthenticationEvent Authenticated)? Read(ClaimsPrincipal token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Guid.TryParse(token.FindFirstValue(JwtRegisteredClaimNames.Sub), out Guid user)
               && Guid.TryParse(token.FindFirstValue(TenantClaim), out Guid tenant)
               && token.FindFirstValue(StampClaim) is { Length: > 0 } stamp
               && long.TryParse(token.FindFirstValue(JwtRegisteredClaimNames.AuthTime), NumberStyles.None, CultureInfo.InvariantCulture, out long at)
            ? (user, tenant, stamp, new AuthenticationEvent(token.FindFirstValue(JwtRegisteredClaimNames.Amr) ?? "unknown", DateTimeOffset.FromUnixTimeSeconds(at)))
            : null;
    }
}
