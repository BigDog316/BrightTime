using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// An account. The ASP.NET Core Identity store columns plus what BrightTime
/// needs.
/// </summary>
/// <remarks>
/// <para>
/// What is <em>not</em> here matters as much as what is. There is no home
/// address and no home coordinate: the search anchor is a neighbourhood or a
/// school, both public geography, and a typed address is geocoded in-request
/// and discarded (docs/plan/08-decisions.md D8).
/// </para>
/// <para>
/// <see cref="DateOfBirthEnc"/> holds the exact date, encrypted, and is
/// decrypted only to evaluate an eligibility rule.
/// <see cref="BirthMonth"/> is the only plaintext derivative — enough for
/// age-band search, not enough to be a date of birth.
/// </para>
/// <para>
/// Email stays in the clear because authentication and delivery need it.
/// Phone does not, so it is encrypted with an HMAC blind index for lookup.
/// </para>
/// </remarks>
public sealed class AppUser
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string UserName { get; set; }

    public required string NormalizedUserName { get; set; }

    public string? Email { get; set; }

    public string? NormalizedEmail { get; set; }

    public bool EmailConfirmed { get; set; }

    public string? PasswordHash { get; set; }

    public required string SecurityStamp { get; set; }

    public required string ConcurrencyStamp { get; set; }

    public byte[]? PhoneEnc { get; set; }

    public byte[]? PhoneBlindIndex { get; set; }

    public short? PhoneKeyId { get; set; }

    public bool PhoneConfirmed { get; set; }

    public bool TwoFactorEnabled { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public bool LockoutEnabled { get; set; } = true;

    public int AccessFailedCount { get; set; }

    public string AccountKind { get; set; } = Domain.AccountKind.Adult;

    public string Status { get; set; } = UserStatus.PendingConsent;

    public string? Locale { get; set; }

    public byte[]? DateOfBirthEnc { get; set; }

    public DateOnly? BirthMonth { get; set; }

    public string? AgeBand { get; set; }

    public Guid? NeighbourhoodId { get; set; }

    public Guid? HomeSchoolId { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset? TermsAcceptedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public UserId UserId => new(Id);

    public bool IsMinorAccount =>
        AccountKind is Domain.AccountKind.Youth or Domain.AccountKind.Child;

    public AgeBand ResolvedAgeBand => AgeBandExtensions.FromDbValue(AgeBand);

    /// <summary>
    /// Whether COPPA's under-13 rules apply. Reads the stored band rather than
    /// recomputing from the encrypted date of birth, and an unknown band counts
    /// as protected — a missing date of birth must not be a route to an
    /// unprotected account.
    /// </summary>
    public bool IsCoppaProtected =>
        AccountKind == Domain.AccountKind.Child || ResolvedAgeBand.IsCoppaProtected();
}

public static class AccountKind
{
    public const string Adult = "adult";

    /// <summary>13 and over. Holds their own credentials and owns their portfolio.</summary>
    public const string Youth = "youth";

    /// <summary>Under 13. Guardian-managed, and never holds a password of its own.</summary>
    public const string Child = "child";
}

public static class UserStatus
{
    public const string PendingConsent = "pending_consent";
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Locked = "locked";
    public const string Deactivated = "deactivated";
    public const string Deleted = "deleted";
}
