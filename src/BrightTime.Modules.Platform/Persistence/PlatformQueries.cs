using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Platform.Domain;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Platform.Persistence;

/// <summary>Taxonomy and geography lookups for other modules, read inside a tenant transaction.</summary>
public sealed class PlatformQueries(PlatformDbContext db) : ITaxonomyQueries, IGeographyQueries, IFeatureFlags
{
    public Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => db.FeatureFlags.AsNoTracking().AnyAsync(f => f.Key == key && f.IsEnabled, ct), cancellationToken);

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

    public Task<IReadOnlyList<VocabularyTerm>> GetVocabularyAsync(
        string vocabulary,
        string locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabulary);

        return db.InTransactionAsync<IReadOnlyList<VocabularyTerm>>(async ct =>
        {
            List<TaxonomyTerm> terms = await db.TaxonomyTerms
                .AsNoTracking()
                .Where(t => t.Vocabulary == vocabulary && t.IsActive)
                .OrderBy(t => t.SortOrder).ThenBy(t => t.Code)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            Guid[] ids = [.. terms.Select(t => t.Id)];
            List<TaxonomyTermName> names = await db.TaxonomyTermNames
                .AsNoTracking()
                .Where(n => ids.Contains(n.TermId) && (n.Locale == locale || n.Locale == FallbackLocale))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return [.. terms.Select(t => new VocabularyTerm(
                t.Id,
                t.Code,
                Pick(names.Where(n => n.TermId == t.Id).Select(n => (n.Locale, n.Name)), locale) ?? t.Code,
                t.ParentId,
                t.SortOrder))];
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "A constant statement; the locale is a bound parameter.")]
    public Task<IReadOnlyList<NeighbourhoodShape>> GetNeighbourhoodsAsync(string locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<NeighbourhoodShape>>(async ct =>
        {
            // Raw SQL for the spatial functions; inside the context's transaction, so under the
            // tenant policy like everything else. Simplified to about 10 m and five decimals:
            // a map outline, a fraction of the full geometry's size.
            System.Data.Common.DbConnection connection = db.Database.GetDbConnection();
            await using System.Data.Common.DbCommand command = connection.CreateCommand();
            command.Transaction = Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction!);
            command.CommandText =
                """
                select n.id, n.code::text,
                       coalesce((select nm.name from platform.neighbourhood_name nm where nm.neighbourhood_id = n.id and nm.locale = @locale),
                                (select nm.name from platform.neighbourhood_name nm where nm.neighbourhood_id = n.id and nm.locale = 'en'),
                                n.code::text),
                       st_asgeojson(st_simplifypreservetopology(n.boundary::geometry, 0.0001), 5),
                       st_x(n.centroid::geometry), st_y(n.centroid::geometry)
                  from platform.neighbourhood n
                 where n.kind = 'neighbourhood' and n.boundary is not null and n.centroid is not null
                   and (n.effective_to is null or n.effective_to >= current_date)
                 order by 3
                """;
            System.Data.Common.DbParameter p = command.CreateParameter();
            p.ParameterName = "locale";
            p.Value = locale;
            command.Parameters.Add(p);

            List<NeighbourhoodShape> shapes = [];
            await using System.Data.Common.DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                shapes.Add(new NeighbourhoodShape(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    Rings(reader.GetString(3)), reader.GetDouble(4), reader.GetDouble(5)));
            }

            return shapes;
        }, cancellationToken);

    /// <summary>The rings of a GeoJSON Polygon or MultiPolygon, in order.</summary>
    internal static IReadOnlyList<IReadOnlyList<(double, double)>> Rings(string geoJson)
    {
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(geoJson);
        System.Text.Json.JsonElement root = doc.RootElement;
        System.Text.Json.JsonElement coordinates = root.GetProperty("coordinates");
        IEnumerable<System.Text.Json.JsonElement> polygons = root.GetProperty("type").GetString() == "MultiPolygon"
            ? coordinates.EnumerateArray()
            : [coordinates];

        List<IReadOnlyList<(double, double)>> rings = [];
        foreach (System.Text.Json.JsonElement polygon in polygons)
        {
            foreach (System.Text.Json.JsonElement ring in polygon.EnumerateArray())
            {
                rings.Add([.. ring.EnumerateArray().Select(pt => (pt[0].GetDouble(), pt[1].GetDouble()))]);
            }
        }

        return rings;
    }

    /// <summary>The requested locale if present, otherwise English, otherwise nothing.</summary>
    private static string? Pick(IEnumerable<(string Locale, string Name)> names, string locale)
    {
        List<(string Locale, string Name)> list = [.. names];
        return list.FirstOrDefault(n => n.Locale == locale).Name
               ?? list.FirstOrDefault(n => n.Locale == FallbackLocale).Name;
    }
}
