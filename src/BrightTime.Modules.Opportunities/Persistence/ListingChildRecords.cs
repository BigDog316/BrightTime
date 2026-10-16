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
