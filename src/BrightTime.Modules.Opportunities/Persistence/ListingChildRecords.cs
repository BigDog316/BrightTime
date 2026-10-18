namespace BrightTime.Modules.Opportunities.Persistence;

/// <summary>A row of <c>opportunity.listing_site</c>: where a listing happens.</summary>
public sealed class ListingSiteRecord
{
    public Guid ListingId { get; init; }

    public Guid SiteId { get; init; }
}

/// <summary>A row of <c>opportunity.listing_term</c>: a category or interest tag.</summary>
public sealed class ListingTermRecord
{
    public Guid ListingId { get; init; }

    public Guid TermId { get; init; }

    public bool IsPrimary { get; set; }
}

/// <summary>A row of <c>opportunity.listing_language</c>.</summary>
public sealed class ListingLanguageRecord
{
    public Guid ListingId { get; init; }

    public required string LanguageCode { get; init; }

    public bool IsInstruction { get; set; } = true;
}

/// <summary>A row of <c>opportunity.listing_accommodation</c>: an accessibility accommodation offered.</summary>
public sealed class ListingAccommodationRecord
{
    public Guid ListingId { get; init; }

    public Guid TermId { get; init; }

    public string? Note { get; set; }
}

/// <summary>A row of <c>opportunity.listing_deadline</c>.</summary>
public sealed class ListingDeadlineRecord
{
    public Guid Id { get; init; }

    public Guid ListingId { get; init; }

    public required string DeadlineKind { get; init; }

    public DateTimeOffset DueAt { get; set; }

    public string? Label { get; set; }
}

/// <summary>A listing's title, summary and description in a locale other than its primary language.</summary>
public sealed class ListingTranslationRecord
{
    public Guid ListingId { get; init; }

    public required string Locale { get; init; }

    public required string Title { get; set; }

    public required string Summary { get; set; }

    public string? Description { get; set; }

    /// <summary>human, machine or machine_reviewed. Machine text is shown, and labelled as such.</summary>
    public string TranslationSource { get; set; } = "human";

    public DateTimeOffset TranslatedAt { get; set; }

    public Guid? TranslatedBy { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>A row of <c>opportunity.listing_revision</c>: the listing's form as it stood when submitted or reviewed.</summary>
public sealed class ListingRevisionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid ListingId { get; init; }

    /// <summary>submitted or reviewed.</summary>
    public required string Kind { get; init; }

    /// <summary>The listing's status once the move was made.</summary>
    public required string Status { get; init; }

    /// <summary>JSON: see <c>ListingRevisions</c>.</summary>
    public required string Snapshot { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedBy { get; init; }
}
