using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Persistence;

/// <summary>Taxonomy and geography lookups for other modules, read inside a tenant transaction.</summary>
public sealed class PlatformQueries(PlatformDbContext db) : ITaxonomyQueries, IGeographyQueries
{
    private const string FallbackLocale = "en";

    public Task<IReadOnlyList<TermSummary>> GetTermsAsync(
        IReadOnlyCollection<Guid> termIds,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(termIds);

        if (termIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<TermSummary>>([]);
        }

        Guid[] ids = [.. termIds.Distinct()];

        return db.InTransactionAsync<IReadOnlyList<TermSummary>>(async ct =>
        {
            List<TaxonomyTerm> terms = await db.TaxonomyTerms
                .AsNoTracking()
                .Where(t => ids.Contains(t.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            List<TaxonomyTermName> names = await db.TaxonomyTermNames
                .AsNoTracking()
                .Where(n => ids.Contains(n.TermId) && (n.Locale == locale || n.Locale == FallbackLocale))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return [.. terms.Select(t => new TermSummary(
                t.Id,
                t.Vocabulary,
                t.Code,
                Pick(names.Where(n => n.TermId == t.Id).Select(n => (n.Locale, n.Name)), locale)))];
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetNeighbourhoodNamesAsync(
        IReadOnlyCollection<Guid> neighbourhoodIds,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neighbourhoodIds);

        if (neighbourhoodIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        }

        Guid[] ids = [.. neighbourhoodIds.Distinct()];

        return db.InTransactionAsync<IReadOnlyDictionary<Guid, string>>(async ct =>
        {
            List<NeighbourhoodName> names = await db.NeighbourhoodNames
                .AsNoTracking()
                .Where(n => ids.Contains(n.NeighbourhoodId) && (n.Locale == locale || n.Locale == FallbackLocale))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Dictionary<Guid, string> result = [];
            foreach (IGrouping<Guid, NeighbourhoodName> group in names.GroupBy(n => n.NeighbourhoodId))
            {
                string? name = Pick(group.Select(n => (n.Locale, n.Name)), locale);
                if (name is not null)
                {
                    result[group.Key] = name;
                }
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>The requested locale if present, otherwise English, otherwise nothing.</summary>
    private static string? Pick(IEnumerable<(string Locale, string Name)> names, string locale)
    {
        List<(string Locale, string Name)> list = [.. names];
        return list.FirstOrDefault(n => n.Locale == locale).Name
               ?? list.FirstOrDefault(n => n.Locale == FallbackLocale).Name;
    }
}
