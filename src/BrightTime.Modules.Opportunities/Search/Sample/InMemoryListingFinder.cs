using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Opportunities.Search.Sample;

/// <summary>
/// A development and test finder over a fixed set of rows. It applies the same
/// filter meanings as the SQL, so the page behaves the same way with no database.
/// </summary>
/// <remarks>
/// <para>
/// Approximations, stated so nobody mistakes this for the real engine. Text matching
/// is every-word-present, case-insensitive substring, not full-text stemming or
/// trigram similarity. There is no origin, so distance is never used. Nearest falls
/// back to relevance, as the SQL does without an origin. Cost, free-only and facet
/// containment match the SQL's meaning.
/// </para>
/// <para>
/// It is not registered in production. The composition root chooses it only when
/// the finder is configured for sample data.
/// </para>
/// </remarks>
public sealed class InMemoryListingFinder(IReadOnlyList<ListingSearchRow> rows, IClock clock) : IListingFinder
{
    private readonly IReadOnlyList<ListingSearchRow> _rows = rows;

    public Task<FinderPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = clock.UtcNow;
        SearchSort sort = EffectiveSort(query);

        List<Scored> ranked = _rows
            .Where(r => r.IsPublished && query.TenantId == KettleCatalogue.TenantId && Matches(r, query))
            .GroupBy(r => r.ListingId)
            .Select(g => g.First())
            .Select(r => Score(r, query, now))
            .ToList();

        List<Scored> ordered = [.. Order(ranked, sort)];

        if (query.After is { } cursor)
        {
            ordered = [.. ordered.Where(x => IsAfter(x, sort, cursor))];
        }

        int pageSize = query.PageSize.Value;
        List<Scored> window = ordered.Take(pageSize + 1).ToList();

        List<FinderItem> items = window.Take(pageSize).Select(x => ToItem(x, sort)).ToList();

        string? nextCursor = window.Count > pageSize
            ? new Cursor(items[^1].SortValue, items[^1].ListingId.Value).Encode()
            : null;

        return Task.FromResult(new FinderPage(items, nextCursor));
    }

    /// <summary>Nearest without an origin is relevance, matching the SQL builder.</summary>
    private static SearchSort EffectiveSort(SearchQuery query) =>
        query.Sort == SearchSort.Nearest ? SearchSort.Relevance : query.Sort;

    private static bool Matches(ListingSearchRow row, SearchQuery query)
    {
        if (query.Kinds is { Count: > 0 } kinds && !kinds.Contains(row.Kind, StringComparer.Ordinal))
        {
            return false;
        }

        if (query.AllFacets is { Count: > 0 } all && !all.All(row.Facets.Contains))
        {
            return false;
        }

        if (query.AnyFacets is { Count: > 0 } any && !any.Any(row.Facets.Contains))
        {
            return false;
        }

        if (query.FreeOnly)
        {
            if (!row.IsFree)
            {
                return false;
            }
        }
        else if (query.MaxCostCents is { } max && row.CostCents > max)
        {
            return false;
        }

        if (query.WindowStart is { } from && query.WindowEnd is { } to)
        {
            if (row.SessionWindowStart is null || row.SessionWindowEnd is null
                || row.SessionWindowStart >= to || row.SessionWindowEnd <= from)
            {
                return false;
            }
        }

        return string.IsNullOrWhiteSpace(query.Text) || TextFraction(row, query.Text) >= 1.0;
    }

    private static Scored Score(ListingSearchRow row, SearchQuery query, DateTimeOffset now)
    {
        double textRank = string.IsNullOrWhiteSpace(query.Text)
            ? 0
            : TextFraction(row, query.Text) * 0.25;

        int overlap = query.InterestTokens is { Count: > 0 } interests
            ? row.Facets.Count(interests.Contains)
            : 0;

        double? days = row.NextStartAt is { } start
            ? Math.Max((start - now).TotalDays, 0)
            : null;

        RankingFeatures features = new(textRank, null, days, overlap, row.Popularity);
        return new Scored(row, features, RelevanceScorer.Score(features));
    }

    private static double TextFraction(ListingSearchRow row, string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return 1.0;
        }

        string haystack = $"{row.Title} {row.Summary} {row.FuzzyText}";
        int matched = words.Count(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
        return (double)matched / words.Length;
    }

    /// <summary>
    /// The sort order, with the id as the tie-breaker. The id direction follows the
    /// sort direction, as in the SQL, so the keyset below is total.
    /// </summary>
    private static IEnumerable<Scored> Order(IEnumerable<Scored> items, SearchSort sort) => sort switch
    {
        SearchSort.Soonest => items
            .OrderBy(x => x.Row.NextStartAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Row.ListingId),

        SearchSort.Newest => items
            .OrderByDescending(x => x.Row.PublishedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.Row.ListingId),

        _ => items
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Row.ListingId),
    };

    /// <summary>
    /// True when an item sorts strictly after the cursor. Written out per sort so
    /// the direction of each comparison is visible, and matched to <see cref="Order"/>.
    /// </summary>
    private static bool IsAfter(Scored item, SearchSort sort, Cursor cursor)
    {
        Guid id = cursor.LastId;

        switch (sort)
        {
            case SearchSort.Soonest:
                {
                    DateTimeOffset at = item.Row.NextStartAt ?? DateTimeOffset.MaxValue;
                    DateTimeOffset cv = ParseTime(cursor.SortValue);
                    return at > cv || (at == cv && item.Row.ListingId > id);
                }

            case SearchSort.Newest:
                {
                    DateTimeOffset at = item.Row.PublishedAt ?? DateTimeOffset.MinValue;
                    DateTimeOffset cv = ParseTime(cursor.SortValue);
                    return at < cv || (at == cv && item.Row.ListingId < id);
                }

            default:
                {
                    double cv = double.Parse(cursor.SortValue, CultureInfo.InvariantCulture);
                    bool sameScore = Math.Abs(item.Score - cv) < 1e-9;
                    return item.Score < cv || (sameScore && item.Row.ListingId < id);
                }
        }
    }

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static FinderItem ToItem(Scored x, SearchSort sort)
    {
        string sortValue = sort switch
        {
            SearchSort.Soonest => (x.Row.NextStartAt ?? DateTimeOffset.MaxValue).ToString("O", CultureInfo.InvariantCulture),
            SearchSort.Newest => (x.Row.PublishedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture),
            _ => x.Score.ToString("0.000000", CultureInfo.InvariantCulture),
        };

        return new FinderItem(
            new ListingId(x.Row.ListingId),
            new OrganisationId(x.Row.OrganisationId),
            ParseKind(x.Row.Kind),
            x.Row.Title,
            x.Row.Summary,
            x.Row.CostCents,
            x.Row.IsFree,
            x.Row.NextStartAt,
            null,
            x.Score,
            RelevanceScorer.Explain(x.Features),
            sortValue);
    }

    private static ListingKind ParseKind(string value) => value switch
    {
        "program" => ListingKind.Program,
        "event" => ListingKind.Event,
        "job" => ListingKind.Job,
        _ => ListingKind.Resource,
    };

    private sealed record Scored(ListingSearchRow Row, RankingFeatures Features, double Score);
}
