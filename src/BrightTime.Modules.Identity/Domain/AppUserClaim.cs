namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// A claim on an account (<c>identity.app_user_claim</c>), ASP.NET Identity's place for profile
/// facts the account table has no column for. A child profile's first name is one.
/// </summary>
public sealed class AppUserClaim
{
    public long Id { get; init; }

    public Guid UserId { get; init; }

    public required string ClaimType { get; init; }

    public required string ClaimValue { get; set; }
}

/// <summary>The claim types this module writes.</summary>
public static class AppUserClaimType
{
    /// <summary>The name a guardian gave a child profile: a first name or a nickname, nothing more.</summary>
    public const string GivenName = "given_name";
}
