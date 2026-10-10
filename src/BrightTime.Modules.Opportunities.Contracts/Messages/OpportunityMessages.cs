using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;

namespace BrightTime.Modules.Opportunities.Contracts.Messages;

/// <summary>A listing reached published. The search projector and the notification fan-out both handle this.</summary>
public sealed record ListingPublished(
    ListingId ListingId,
    OrganisationId OrganisationId,
    DateTimeOffset PublishedAt) : IModuleMessage
{
    public static string MessageType => "opportunities.listing.published";
}

/// <summary>A published listing left publication. Its search rows must be hidden, not deleted.</summary>
public sealed record ListingUnpublished(
    ListingId ListingId,
    OrganisationId OrganisationId,
    string Reason,
    DateTimeOffset UnpublishedAt) : IModuleMessage
{
    public static string MessageType => "opportunities.listing.unpublished";
}

/// <summary>A published listing's searchable content changed. Triggers reprojection, debounced on the listing id.</summary>
public sealed record ListingUpdated(
    ListingId ListingId,
    DateTimeOffset UpdatedAt) : IModuleMessage
{
    public static string MessageType => "opportunities.listing.updated";
}
