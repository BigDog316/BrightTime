using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Curation;

/// <summary>
/// The public side of curation: published collections and neighbourhood pages, in the reader's
/// language where the organiser wrote one, falling back to the original.
/// </summary>
/// <remarks>
/// A collection is shown only while published and within its dates (in the tenant's zone). Its
/// entries are listing ids in the organiser's order; the page fetches them through the finder,
/// which shows only what is published, so a collection never leaks a draft or an unpublished
/// listing however it was curated.
/// </remarks>
public sealed class CurationQueries(OpportunitiesDbContext db, IClock clock, Infrastructure.Tenancy.ITenantContext tenant) : ICurationQueries
{
    private const string Published = "published";

    public Task<CollectionView?> GetCollectionAsync(string slug, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            DateOnly today = clock.TodayIn(tenant.TimeZone);
            CollectionRecord? c = await db.Collections.AsNoTracking()
                .Where(x => x.Slug == slug && x.Status == Published)
                .Where(x => (x.StartsOn == null || x.StartsOn <= today) && (x.EndsOn == null || x.EndsOn >= today))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (c is null)
            {
                return null;
            }

            CollectionTranslationRecord? t = await db.CollectionTranslations.AsNoTracking()
                .FirstOrDefaultAsync(x => x.CollectionId == c.Id && x.Locale == locale, ct).ConfigureAwait(false);
            List<CollectionEntry> entries = await db.CollectionItems.AsNoTracking()
                .Where(i => i.CollectionId == c.Id)
                .OrderBy(i => i.SortOrder).ThenBy(i => i.ListingId)
                .Select(i => new CollectionEntry(i.ListingId, i.CuratorNote))
                .ToListAsync(ct).ConfigureAwait(false);

            return new CollectionView(c.Id, c.Slug, t?.Title ?? c.Title, t is null ? c.Description : t.Description,
                t is null ? SourceLanguage : locale, entries);
        }, cancellationToken);

    public Task<IReadOnlyList<CollectionSummary>> ListCollectionsAsync(string locale, bool featuredOnly, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<CollectionSummary>>(async ct =>
        {
            DateOnly today = clock.TodayIn(tenant.TimeZone);
            var rows = await db.Collections.AsNoTracking()
                .Where(x => x.Status == Published && (!featuredOnly || x.IsFeatured))
                .Where(x => (x.StartsOn == null || x.StartsOn <= today) && (x.EndsOn == null || x.EndsOn >= today))
                .OrderBy(x => x.SortOrder).ThenBy(x => x.Title)
                .Select(x => new
                {
                    x.Id,
                    x.Slug,
                    x.Title,
                    x.Description,
                    Translation = db.CollectionTranslations.Where(t => t.CollectionId == x.Id && t.Locale == locale).Select(t => new { t.Title, t.Description }).FirstOrDefault(),
                    Count = db.CollectionItems.Count(i => i.CollectionId == x.Id),
                })
                .ToListAsync(ct).ConfigureAwait(false);

            return [.. rows.Select(r => new CollectionSummary(
                r.Id, r.Slug, r.Translation?.Title ?? r.Title, r.Translation is null ? r.Description : r.Translation.Description,
                r.Translation is null ? SourceLanguage : locale, r.Count))];
        }, cancellationToken);

    public Task<NeighbourhoodPageView?> GetNeighbourhoodPageAsync(Guid neighbourhoodId, string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            NeighbourhoodPageRecord? page = await db.NeighbourhoodPages.AsNoTracking()
                .FirstOrDefaultAsync(p => p.NeighbourhoodId == neighbourhoodId && p.Status == Published, ct).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            List<NeighbourhoodPageBlockRecord> blocks = await db.NeighbourhoodPageBlocks.AsNoTracking()
                .Where(b => b.PageId == page.Id && (b.Locale == locale || b.Locale == SourceLanguage))
                .OrderBy(b => b.SortOrder).ThenBy(b => b.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            // The page in the reader's language if the organiser wrote one, otherwise in English,
            // whole: a page mixing languages block by block reads worse than either.
            string language = blocks.Any(b => b.Locale == locale) ? locale : SourceLanguage;
            return new NeighbourhoodPageView(neighbourhoodId, language,
                [.. blocks.Where(b => b.Locale == language).Select(b => new PageBlock(b.BlockKind, b.Heading, b.BodyMd, b.Payload))]);
        }, cancellationToken);

    /// <summary>Untranslated collection and page text is the organiser's, in English.</summary>
    private const string SourceLanguage = "en";
}
