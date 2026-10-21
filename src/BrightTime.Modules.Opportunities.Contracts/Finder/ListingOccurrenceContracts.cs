namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>One meeting of one of an organisation's published listings.</summary>
public sealed record OrganisationMeeting(Guid ListingId, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// A listing's meetings as other modules need them: attendance is taken for one occurrence, and
/// only for one that exists and isn't cancelled (D49).
/// </summary>
public interface IListingOccurrences
{
    /// <summary>The listing's meetings starting in [<paramref name="from"/>, <paramref name="to"/>), cancelled ones left out, earliest first.</summary>
    Task<IReadOnlyList<ListingTime>> GetAsync(Guid listingId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    /// <summary>The meetings of an organisation's published listings starting in the window, earliest first: a staff member's day.</summary>
    Task<IReadOnlyList<OrganisationMeeting>> ForOrganisationAsync(Guid orgId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
