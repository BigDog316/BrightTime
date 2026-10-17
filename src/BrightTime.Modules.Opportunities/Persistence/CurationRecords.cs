namespace BrightTime.Modules.Opportunities.Persistence;

/// <summary><c>opportunity.collection</c>: an organiser's curated list of listings.</summary>
public sealed class CollectionRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Slug { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public Guid? HeroMediaId { get; set; }

    public Guid? CuratorUserId { get; set; }

    public string Status { get; set; } = "draft";

    public DateOnly? StartsOn { get; set; }

    public DateOnly? EndsOn { get; set; }

    public bool IsFeatured { get; set; }

    public short SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CollectionItemRecord
{
    public Guid CollectionId { get; init; }

    public Guid ListingId { get; init; }

    public short SortOrder { get; set; }

    public string? CuratorNote { get; set; }
}

public sealed class CollectionTranslationRecord
{
    public Guid CollectionId { get; init; }

    public required string Locale { get; init; }

    public required string Title { get; set; }

    public string? Description { get; set; }
}

/// <summary><c>opportunity.neighbourhood_page</c>: an organiser's page for one neighbourhood.</summary>
public sealed class NeighbourhoodPageRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid NeighbourhoodId { get; init; }

    public string Status { get; set; } = "draft";

    public Guid? HeroMediaId { get; set; }

    public Guid? OrganiserUserId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One block of a neighbourhood page, in one locale.</summary>
public sealed class NeighbourhoodPageBlockRecord
{
    public Guid Id { get; init; }

    public Guid PageId { get; init; }

    public required string Locale { get; init; }

    public required string BlockKind { get; init; }

    public short SortOrder { get; set; }

    public string? Heading { get; set; }

    public string? BodyMd { get; set; }

    /// <summary>JSON whose shape depends on the kind: contacts, questions, a collection's slug.</summary>
    public string Payload { get; set; } = "{}";
}
