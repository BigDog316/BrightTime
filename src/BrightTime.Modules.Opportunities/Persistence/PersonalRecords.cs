namespace BrightTime.Modules.Opportunities.Persistence;

// A signed-in person's own finder data: what they saved, what they are into, when to remind them,
// and where they search from. Reached through the person's own account only (row-level security
// requires an actor; the services check whose rows they are).

/// <summary><c>opportunity.saved_item</c>: a listing someone kept to come back to.</summary>
public sealed class SavedItemRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public Guid ListingId { get; init; }

    public string? Note { get; set; }

    public DateTimeOffset SavedAt { get; init; }
}

/// <summary><c>opportunity.user_interest</c>: a declared interest, weighted 1 to 5. Feeds the feed's ranking and its "why".</summary>
public sealed class UserInterestRecord
{
    public Guid UserId { get; init; }

    public Guid TermId { get; init; }

    public short Weight { get; set; }

    public DateTimeOffset DeclaredAt { get; set; }
}

/// <summary><c>opportunity.listing_reminder</c>: a reminder before a deadline or a session.</summary>
public sealed class ListingReminderRecord
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public Guid UserId { get; init; }

    public Guid ListingId { get; init; }

    public required string Anchor { get; init; }

    public int LeadMinutes { get; init; }

    public DateTimeOffset RemindAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>
/// <c>opportunity.user_search_preference</c>: where someone searches from (a neighbourhood or a
/// school, never an address) and what they need.
/// </summary>
public sealed class UserSearchPreferenceRecord
{
    public Guid UserId { get; init; }

    public Guid TenantId { get; init; }

    public string AnchorKind { get; set; } = "none";

    public Guid? AnchorNeighbourhoodId { get; set; }

    public Guid? AnchorSchoolId { get; set; }

    public int? MaxDistanceM { get; set; }

    public string[] PreferredLanguages { get; set; } = [];

    public Guid[] NeedsAccommodationTerms { get; set; } = [];

    public bool LowBandwidth { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
