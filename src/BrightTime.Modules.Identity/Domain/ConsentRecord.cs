namespace BrightTime.Modules.Identity.Domain;

/// <summary>
/// A person's own agreement to a document (<c>identity.consent_record</c>): the terms of use, the
/// privacy notice. Not a guardian's consent for a child, which is <see cref="ParentalConsent"/>.
/// </summary>
/// <remarks>
/// <see cref="DocumentHash"/> pins the exact words agreed to, as a parental consent's hash does,
/// so a later version of the document is never taken as agreed.
/// </remarks>
public sealed class ConsentRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public required string DocumentKind { get; init; }

    public required string DocumentVersion { get; init; }

    public required byte[] DocumentHash { get; init; }

    public DateTimeOffset AcceptedAt { get; init; }

    public byte[]? IpHash { get; init; }
}

/// <summary>The kinds <c>ck_consent_record_kind</c> allows.</summary>
public static class ConsentDocumentKind
{
    public const string Terms = "terms";
}
