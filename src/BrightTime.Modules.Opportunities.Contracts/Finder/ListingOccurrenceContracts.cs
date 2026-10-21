namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>
/// A listing's meetings as other modules need them: attendance is taken for one occurrence, and
/// only for one that exists and isn't cancelled (D49).
/// </summary>
public interface IListingOccurrences
{
    /// <summary>The listing's meetings starting in [<paramref name="from"/>, <paramref name="to"/>), cancelled ones left out, earliest first.</summary>
    Task<IReadOnlyList<ListingTime>> GetAsync(Guid listingId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}
