using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Details;

/// <summary>
/// The public listing page's data, read from the database under the tenant's row-level security.
/// </summary>
/// <remarks>
/// Published and unexpired only, decided in the query rather than after it: a draft must
/// never be loaded into the page's memory, let alone rendered. Street addresses come from
/// Directory, which withholds them for a private site; this class cannot see them.
/// </remarks>
public sealed class SqlListingDetails(
    OpportunitiesDbContext db,
    IDirectoryQueries directory,
    ITaxonomyQueries taxonomy,
    IGeographyQueries geography,
    IClock clock) : IListingDetails
{
    /// <summary>How many upcoming sessions the page lists.</summary>
    public const int UpcomingShown = 10;

    public async Task<ListingDetail?> GetPublishedAsync(
        TenantId tenant,
        string slug,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        DateTimeOffset now = clock.UtcNow;
        string published = ListingStatus.Published.ToDbValue();

        return await ReadAsync(
            l => l.TenantId == tenant.Value
                 && l.Slug == slug
                 && l.Status == published
                 && (l.PublishAt == null || l.PublishAt <= now)
                 && (l.ExpiresAt == null || l.ExpiresAt > now),
            locale, cancellationToken).ConfigureAwait(false);
    }

    public Task<ListingDetail?> GetForReviewAsync(
        TenantId tenant,
        Guid listingId,
        string locale,
        CancellationToken cancellationToken = default) =>
        ReadAsync(l => l.TenantId == tenant.Value && l.Id == listingId, locale, cancellationToken);

    private async Task<ListingDetail?> ReadAsync(
        System.Linq.Expressions.Expression<Func<ListingRecord, bool>> which,
        string locale,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;

        Loaded? loaded = await db.InTransactionAsync(async ct =>
        {
            ListingRecord? listing = await db.Listings.AsNoTracking()
                .Where(which)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

            if (listing is null)
            {
                return null;
            }

            Guid id = listing.Id;
            ListingTranslationRecord? translation = string.Equals(locale, listing.PrimaryLanguage, StringComparison.OrdinalIgnoreCase)
                ? null
                : await db.ListingTranslations.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.ListingId == id && t.Locale == locale, ct).ConfigureAwait(false);

            return new Loaded(
                listing,
                translation,
                await db.ListingSites.AsNoTracking().Where(x => x.ListingId == id).Select(x => x.SiteId).ToListAsync(ct).ConfigureAwait(false),
                await db.ListingTerms.AsNoTracking().Where(x => x.ListingId == id).Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false),
                await db.ListingAccommodations.AsNoTracking().Where(x => x.ListingId == id).Select(x => x.TermId).ToListAsync(ct).ConfigureAwait(false),
                await db.ListingLanguages.AsNoTracking().Where(x => x.ListingId == id).Select(x => x.LanguageCode).ToListAsync(ct).ConfigureAwait(false),
                await db.Occurrences.AsNoTracking()
                    .Where(o => o.ListingId == id && !o.IsCancelled && o.StartsAt >= now)
                    .OrderBy(o => o.StartsAt).Take(UpcomingShown)
                    .Select(o => new ListingTime(o.StartsAt, o.EndsAt))
                    .ToListAsync(ct).ConfigureAwait(false),
                await db.ListingDeadlines.AsNoTracking()
                    .Where(d => d.ListingId == id && d.DueAt >= now)
                    .OrderBy(d => d.DueAt)
                    .Select(d => new ListingDeadlineView(d.DeadlineKind, d.DueAt, d.Label))
                    .ToListAsync(ct).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (loaded is null)
        {
            return null;
        }

        ListingRecord l = loaded.Listing;
        ListingTranslationRecord? tr = loaded.Translation;
        string contentLanguage = tr?.Locale ?? l.PrimaryLanguage;

        IReadOnlyList<SiteSummary> sites = await directory.GetSitesAsync(loaded.SiteIds, cancellationToken).ConfigureAwait(false);
        string organisationName = await directory
            .GetOrganisationNameAsync(new OrganisationId(l.OrgId), cancellationToken).ConfigureAwait(false) ?? string.Empty;

        IReadOnlyList<TermSummary> terms = await taxonomy
            .GetTermsAsync([.. loaded.TermIds, .. loaded.AccommodationTermIds], locale, cancellationToken).ConfigureAwait(false);

        List<SiteSummary> activeSites = [.. sites.Where(s => s.IsActive)];
        Guid[] hoods = [.. activeSites.Where(s => s.NeighbourhoodId is not null).Select(s => s.NeighbourhoodId!.Value)];
        IReadOnlyDictionary<Guid, string> hoodNames = await geography
            .GetNeighbourhoodNamesAsync(hoods, locale, cancellationToken).ConfigureAwait(false);

        HashSet<Guid> accommodationIds = [.. loaded.AccommodationTermIds];

        return new ListingDetail
        {
            Id = new ListingId(l.Id),
            Slug = l.Slug,
            Kind = ParseKind(l.Kind),
            Title = tr?.Title ?? l.Title,
            Summary = tr?.Summary ?? l.Summary,

            // A translation without a description falls back to the original, marked as such.
            Description = tr?.Description ?? l.Description,
            ContentLanguage = contentLanguage,
            DescriptionLanguage = tr?.Description is not null ? contentLanguage : l.PrimaryLanguage,
            PrimaryLanguage = l.PrimaryLanguage,
            IsMachineTranslated = tr is { TranslationSource: "machine" },
            OrganisationName = organisationName,
            OrganisationId = new OrganisationId(l.OrgId),
            AgeMin = l.AgeMin,
            AgeMax = l.AgeMax,
            GradeMin = l.GradeMin,
            GradeMax = l.GradeMax,
            CostModel = Parse<CostModel>(l.CostModel, c => c.ToDbValue()),
            CostCentsMin = l.CostCentsMin,
            CostCentsMax = l.CostCentsMax,
            SlidingScaleNote = l.SlidingScaleNote,
            Modality = Parse<Modality>(l.Modality, m => m.ToDbValue()),
            RegistrationMode = Parse<RegistrationMode>(l.RegistrationMode, r => r.ToDbValue()),
            ExternalUrl = IsLinkable(l.ExternalUrlState) ? l.ExternalUrl : null,
            MealsProvided = l.MealsProvided,
            TransportProvided = l.TransportProvided,
            TransportNote = l.TransportNote,
            IsDropIn = l.IsDropIn,
            AccessibilityNote = l.AccessibilityNote,
            ContactEmail = l.ContactEmail,
            ContactPhone = l.ContactPhone,
            Categories = [.. terms.Where(t => !accommodationIds.Contains(t.Id)).Select(t => t.Name ?? t.Code)],
            Accommodations = [.. terms.Where(t => accommodationIds.Contains(t.Id)).Select(t => t.Name ?? t.Code)],
            Languages = loaded.Languages,
            Places = [.. activeSites.Select(s => new ListingPlace(
                s.Name,
                s.IsAddressPublic,
                s.AddressLine1,
                s.AddressLine2,
                s.Locality,
                s.PostalCode,
                s.NeighbourhoodId is { } h && hoodNames.TryGetValue(h, out string? name) ? name : null,
                s.TransitNote,
                s.IsWheelchairAccessible,
                s.NeighbourhoodId))],
            Upcoming = loaded.Upcoming,
            Deadlines = loaded.Deadlines,
        };
    }

    private sealed record Loaded(
        ListingRecord Listing,
        ListingTranslationRecord? Translation,
        IReadOnlyList<Guid> SiteIds,
        IReadOnlyList<Guid> TermIds,
        IReadOnlyList<Guid> AccommodationTermIds,
        IReadOnlyList<string> Languages,
        IReadOnlyList<ListingTime> Upcoming,
        IReadOnlyList<ListingDeadlineView> Deadlines);

    /// <summary>
    /// A link the URL checker found broken or blocked is withheld: sending a family to a dead
    /// or flagged page is worse than telling them to contact the provider. Unchecked links are
    /// shown, because every new listing starts unchecked.
    /// </summary>
    internal static bool IsLinkable(string urlState) => urlState is not ("broken" or "blocked");

    private static ListingKind ParseKind(string value) => Parse<ListingKind>(value, k => k.ToDbValue());

    private static T Parse<T>(string value, Func<T, string> toDb) where T : struct, Enum
    {
        foreach (T candidate in Enum.GetValues<T>())
        {
            if (string.Equals(toDb(candidate), value, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"'{value}' is not a valid {typeof(T).Name} in the listing table.");
    }
}
