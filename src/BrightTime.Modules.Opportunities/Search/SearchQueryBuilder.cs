using System.Globalization;
using System.Text;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Paging;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>A composed statement and the values it binds. Values never appear in <see cref="Sql"/>.</summary>
public sealed record BuiltQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// Composes the finder's SQL from only the filters a caller supplied.
/// </summary>
/// <remarks>
/// <para>
/// The statement contains no caller-supplied text. Every value is bound as a
/// named parameter, and the SQL is assembled from fixed fragments chosen by
/// which filters are present. A query with no origin has no <c>st_dwithin</c>
/// predicate at all, rather than a <c>(:origin is null or …)</c> guard, which
/// keeps the planner from costing a dead branch and keeps each filter shape
/// with its own cached plan.
/// </para>
/// <para>
/// Keyset pagination only. The comparison and the id direction always agree
/// with the sort direction, so tied scores or equal start times cannot skip or
/// repeat rows across a page boundary.
/// </para>
/// </remarks>
public static class SearchQueryBuilder
{
    /// <summary>
    /// The origin as a geography point, built from two bound numbers. Inline
    /// rather than a bound geometry, so the caller never has to serialise a
    /// spatial type, and the distance and radius predicates share one definition.
    /// </summary>
    internal const string OriginExpression =
        "st_setsrid(st_makepoint(@origin_lon, @origin_lat), 4326)::geography";

    public static BuiltQuery Build(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant_id"] = query.TenantId,
            ["page_size"] = query.PageSize.FetchCount,
        };

        var predicates = new StringBuilder();
        AddFilterPredicates(query, predicates, parameters);

        bool hasText = !string.IsNullOrWhiteSpace(query.Text);
        bool hasOrigin = query.Origin is not null;
        string sort = SortClause(query.Sort, hasOrigin);

        var sql = new StringBuilder();
        sql.Append("with matched as (\n");
        sql.Append("  select ls.listing_id, ls.site_id, ls.slug, ls.title, ls.summary, ls.org_id, ls.kind,\n");
        sql.Append("         ls.cost_cents, ls.is_free, ls.modality, ls.neighbourhood_id,\n");
        sql.Append("         ls.next_start_at, ls.next_deadline_at, ls.popularity, ls.published_at, ls.is_drop_in,\n");
        sql.Append(hasOrigin
            ? "         st_distance(ls.location, " + OriginExpression + ") as distance_m,\n"
            : "         null::double precision as distance_m,\n");
        sql.Append(hasText
            ? "         ts_rank_cd(ls.search_vector, websearch_to_tsquery(@ts_config::regconfig, @text), 32) as text_rank,\n"
            : "         0::real as text_rank,\n");
        sql.Append(query.InterestTokens is { Count: > 0 }
            ? "         (select count(*) from unnest(ls.facets) f where f = any(@interest_tokens)) as interest_overlap\n"
            : "         0 as interest_overlap\n");
        sql.Append("  from opportunity.listing_search ls\n");
        sql.Append("  where ls.tenant_id = @tenant_id and ls.is_published\n");
        sql.Append(predicates);
        sql.Append("),\n");
        sql.Append("best_site as (\n");
        sql.Append("  select distinct on (listing_id) *\n");
        sql.Append("  from matched\n");
        sql.Append("  order by listing_id, distance_m asc nulls last\n");
        sql.Append("),\n");
        sql.Append("scored as (\n");
        sql.Append("  select bs.*, ").Append(ScoreExpression(hasOrigin)).Append(" as score\n");
        sql.Append("  from best_site bs\n");
        sql.Append(")\n");
        sql.Append("select s.listing_id, s.site_id, s.slug, s.title, s.summary, s.org_id, s.kind,\n");
        sql.Append("       s.cost_cents, s.is_free, s.modality, s.neighbourhood_id,\n");
        sql.Append("       s.next_start_at, s.next_deadline_at, s.published_at,\n");
        sql.Append("       s.distance_m, s.score, s.interest_overlap, s.text_rank, s.popularity,\n");
        sql.Append("       extract(epoch from (s.next_start_at - now())) / 86400.0 as days_until_start\n");
        sql.Append("from scored s\n");

        if (query.After is { } cursor)
        {
            sql.Append("where ").Append(CursorPredicate(query.Sort, hasOrigin)).Append('\n');
            parameters["cursor_value"] = cursor.SortValue;
            parameters["cursor_id"] = cursor.LastId;
        }

        sql.Append("order by ").Append(sort).Append('\n');
        sql.Append("limit @page_size");

        if (hasText)
        {
            parameters["text"] = query.Text!.Trim();
            parameters["ts_config"] = query.TextSearchConfig;
        }

        if (hasOrigin)
        {
            parameters["origin_lat"] = query.Origin!.Value.Latitude;
            parameters["origin_lon"] = query.Origin!.Value.Longitude;
        }

        return new BuiltQuery(sql.ToString(), parameters);
    }

    /// <summary>
    /// The neighbourhood counts for the map: the search's filters, grouped by neighbourhood, with
    /// online-only rows (no site) apart.
    /// </summary>
    public static BuiltQuery BuildNeighbourhoodCounts(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tenant_id"] = query.TenantId };
        var predicates = new StringBuilder();
        AddFilterPredicates(query, predicates, parameters);
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            parameters["text"] = query.Text!.Trim();
            parameters["ts_config"] = query.TextSearchConfig;
        }

        if (query.Origin is not null)
        {
            parameters["origin_lat"] = query.Origin!.Value.Latitude;
            parameters["origin_lon"] = query.Origin!.Value.Longitude;
        }

        var sql = new StringBuilder();
        sql.Append("select ls.site_id is null as online, ls.neighbourhood_id,\n");
        sql.Append("       count(distinct ls.listing_id)::int as listings\n");
        sql.Append("from opportunity.listing_search ls\n");
        sql.Append("where ls.tenant_id = @tenant_id and ls.is_published\n");
        sql.Append(predicates);
        sql.Append("group by 1, 2");
        return new BuiltQuery(sql.ToString(), parameters);
    }

    private static void AddFilterPredicates(SearchQuery query, StringBuilder predicates, Dictionary<string, object?> parameters)
    {
        if (query.Kinds is { Count: > 0 } kinds)
        {
            predicates.Append("    and ls.kind = any(@kinds)\n");
            parameters["kinds"] = kinds.ToArray();
        }

        if (query.OrganisationId is { } organisation)
        {
            predicates.Append("    and ls.org_id = @org_id\n");
            parameters["org_id"] = organisation;
        }

        if (query.ListingIds is { } listingIds)
        {
            // An empty list matches nothing, rather than everything.
            predicates.Append("    and ls.listing_id = any(@listing_ids)\n");
            parameters["listing_ids"] = listingIds.ToArray();
        }

        if (query.AllFacets is { Count: > 0 } all)
        {
            predicates.Append("    and ls.facets @> @facets_all\n");
            parameters["facets_all"] = all.ToArray();
        }

        if (query.AnyFacets is { Count: > 0 } any)
        {
            predicates.Append("    and ls.facets && @facets_any\n");
            parameters["facets_any"] = any.ToArray();
        }

        if (query.WindowStart is { } from && query.WindowEnd is { } to)
        {
            predicates.Append("    and ls.session_window && tstzrange(@window_start, @window_end)\n");
            parameters["window_start"] = from;
            parameters["window_end"] = to;
        }

        if (query.FreeOnly)
        {
            predicates.Append("    and ls.is_free\n");
        }
        else if (query.MaxCostCents is { } maxCost)
        {
            predicates.Append("    and ls.cost_cents <= @max_cost_cents\n");
            parameters["max_cost_cents"] = maxCost;
        }

        if (query.Origin is not null && query.Radius is { } radius)
        {
            predicates.Append("    and st_dwithin(ls.location, " + OriginExpression + ", @radius_m)\n");
            parameters["radius_m"] = radius.Metres;
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            predicates.Append("    and (ls.search_vector @@ websearch_to_tsquery(@ts_config::regconfig, @text)\n");
            predicates.Append("         or ls.fuzzy_text % @text)\n");
        }
    }

    private static string ScoreExpression(bool hasOrigin)
    {
        string distance = hasOrigin
            ? "case when distance_m is null then 0.5 else exp(-distance_m / 4000.0) end"
            : "0.5";

        return string.Create(CultureInfo.InvariantCulture,
            $"round((0.45 * least(text_rank * 4.0, 1.0)" +
            $" + 0.20 * ({distance})" +
            $" + 0.15 * (case when next_start_at is null then 0.3" +
            $" else exp(-greatest(extract(epoch from (next_start_at - now())) / 86400.0, 0) / 30.0) end)" +
            $" + 0.12 * least(interest_overlap / 3.0, 1.0)" +
            $" + 0.08 * least(popularity / 100.0, 1.0))::numeric, 6)");
    }

    /// <summary>
    /// The ORDER BY for a sort. The id direction is part of the sort, so a
    /// keyset comparison on the pair is total and stable.
    /// </summary>
    private static string SortClause(SearchSort sort, bool hasOrigin) => sort switch
    {
        SearchSort.Soonest => "s.next_start_at asc, s.listing_id asc",
        SearchSort.Nearest when hasOrigin => "s.distance_m asc nulls last, s.listing_id asc",
        SearchSort.Newest => "s.published_at desc, s.listing_id desc",
        _ => "s.score desc, s.listing_id desc",
    };

    /// <summary>The keyset predicate for a sort. Must match <see cref="SortClause"/> exactly.</summary>
    private static string CursorPredicate(SearchSort sort, bool hasOrigin) => sort switch
    {
        SearchSort.Soonest => "s.next_start_at is not null and (s.next_start_at, s.listing_id) > (@cursor_value::timestamptz, @cursor_id::uuid)",
        SearchSort.Nearest when hasOrigin => "s.distance_m is not null and (s.distance_m, s.listing_id) > (@cursor_value::double precision, @cursor_id::uuid)",
        SearchSort.Newest => "(s.published_at, s.listing_id) < (@cursor_value::timestamptz, @cursor_id::uuid)",
        _ => "(s.score, s.listing_id) < (@cursor_value::numeric, @cursor_id::uuid)",
    };
}
