using BrightTime.Modules.Directory.Contracts;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Domain;

/// <summary>
/// A provider organisation.
/// </summary>
/// <remarks>
/// The EIN is encrypted with a blind index for lookup — it is a tax
/// identifier, and holding it in the clear buys nothing. Insurance expiry is a
/// plain date because the organiser dashboard needs to sort by it.
/// </remarks>
public sealed class Organisation
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string LegalName { get; set; }

    public required string DisplayName { get; set; }

    public required string Slug { get; set; }

    public string Status { get; set; } = OrganisationStatus.Applied.ToDbValue();

    public required string OrgKind { get; set; }

    public byte[]? EinEnc { get; set; }

    public byte[]? EinBlindIndex { get; set; }

    public string? WebsiteUrl { get; set; }

    public string? Mission { get; set; }

    public Guid? LogoMediaId { get; set; }

    public Guid? PrimaryContactUserId { get; set; }

    public string? PublicEmail { get; set; }

    public string? PublicPhone { get; set; }

    public string[] LanguagesServed { get; set; } = [];

    public DateTimeOffset? SubmittedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public Guid? ReviewedBy { get; set; }

    public string? ReviewNote { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset? SuspendedAt { get; set; }

    public string? SuspensionReason { get; set; }

    public DateOnly? InsuranceExpiresOn { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public OrganisationId OrganisationId => new(Id);

    public OrganisationStatus ResolvedStatus =>
        OrganisationStatusExtensions.TryFromDbValue(Status, out OrganisationStatus s)
            ? s
            : OrganisationStatus.Draft;
}

public static class OrganisationKind
{
    public const string Nonprofit = "nonprofit";
    public const string PublicAgency = "public_agency";
    public const string School = "school";
    public const string Library = "library";
    public const string FaithBased = "faith_based";
    public const string Business = "business";
    public const string HigherEd = "higher_ed";
    public const string InformalGroup = "informal_group";

    public static readonly string[] All =
        [Nonprofit, PublicAgency, School, Library, FaithBased, Business, HigherEd, InformalGroup];
}
