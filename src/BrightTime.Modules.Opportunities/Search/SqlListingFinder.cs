using System.Data;
using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using Npgsql;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>
/// The production finder: runs <see cref="SearchQueryBuilder"/>'s statement against
/// <c>opportunity.listing_search</c> under the tenant's row-level security.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not yet verified against a database.</strong> The statement is tested for
/// its shape and its parameter binding, not for its results or its plan. It runs
/// once PostGIS is installed and the EXPLAIN harness is in place.
/// </para>
/// <para>
/// The tenant is set with <c>set_config(..., is_local =&gt; true)</c> inside the same
/// transaction as the query, mirroring <c>TenantSessionInterceptor</c>. A pooled
/// connection cannot carry one request's tenant into the next.
/// </para>
/// </remarks>
public sealed class SqlListingFinder(NpgsqlDataSource dataSource) : IListingFinder
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The statement is composed by SearchQueryBuilder from fixed fragments only; every " +
                        "caller value is bound as a parameter. SearchQueryBuilderTests asserts that hostile " +
                        "input never reaches the statement text.")]
    public async Task<FinderPage> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        BuiltQuery built = SearchQueryBuilder.Build(query);

        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await SetTenantAsync(connection, transaction, query.TenantId, cancellationToken).ConfigureAwait(false);

        List<FinderItem> rows = [];
        await using (NpgsqlCommand command = new(built.Sql, connection, transaction))
        {
            foreach ((string name, object? value) in built.Parameters)
            {
                command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
            }

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadItem(reader, query.Sort));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The query fetches one row more than a page, to learn whether there is a next page.
        int pageSize = query.PageSize.Value;
        if (rows.Count <= pageSize)
        {
            return new FinderPage(rows, null);
        }

        List<FinderItem> page = rows.Take(pageSize).ToList();
        FinderItem last = page[^1];
        string nextCursor = new Cursor(last.SortValue, last.ListingId.Value).Encode();

        return new FinderPage(page, nextCursor);
    }

    private static async Task SetTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "select set_config('brighttime.tenant_id', @tenant, true), set_config('brighttime.actor_id', '', true)",
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter("tenant", tenantId.ToString("D", CultureInfo.InvariantCulture)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static FinderItem ReadItem(NpgsqlDataReader reader, SearchSort sort)
    {
        Guid listingId = reader.GetGuid(reader.GetOrdinal("listing_id"));
        Guid orgId = reader.GetGuid(reader.GetOrdinal("org_id"));
        string kindText = reader.GetString(reader.GetOrdinal("kind"));
        bool isFree = reader.GetBoolean(reader.GetOrdinal("is_free"));
        int costCents = reader.GetInt32(reader.GetOrdinal("cost_cents"));
        DateTimeOffset? nextStart = ReadNullable<DateTimeOffset>(reader, "next_start_at");
        DateTimeOffset? publishedAt = ReadNullable<DateTimeOffset>(reader, "published_at");
        double? distance = ReadNullable<double>(reader, "distance_m");
        double score = reader.GetFieldValue<double>(reader.GetOrdinal("score"));
        double textRank = reader.GetFieldValue<double>(reader.GetOrdinal("text_rank"));
        long interestOverlap = reader.GetFieldValue<long>(reader.GetOrdinal("interest_overlap"));
        double popularity = reader.GetFieldValue<double>(reader.GetOrdinal("popularity"));
        double? daysUntilStart = ReadNullable<double>(reader, "days_until_start");

        RankingFeatures features = new(textRank, distance, daysUntilStart, (int)interestOverlap, popularity);

        string sortValue = sort switch
        {
            SearchSort.Soonest => (nextStart ?? DateTimeOffset.MaxValue).ToString("O", CultureInfo.InvariantCulture),
            SearchSort.Nearest when distance is not null => distance.Value.ToString("R", CultureInfo.InvariantCulture),
            SearchSort.Newest => (publishedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture),
            _ => score.ToString("0.000000", CultureInfo.InvariantCulture),
        };

        return new FinderItem(
            new ListingId(listingId),
            new OrganisationId(orgId),
            ParseKind(kindText),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetString(reader.GetOrdinal("summary")),
            costCents,
            isFree,
            nextStart,
            distance,
            score,
            RelevanceScorer.Explain(features),
            sortValue);
    }

    /// <summary>
    /// Typed read. <c>GetFieldValue</c> rather than a cast: Npgsql returns
    /// <c>timestamptz</c> through <c>GetValue</c> as <see cref="DateTime"/>, and a cast
    /// to <see cref="DateTimeOffset"/> fails at run time.
    /// </summary>
    private static T? ReadNullable<T>(NpgsqlDataReader reader, string column) where T : struct
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static ListingKind ParseKind(string value) => value switch
    {
        "program" => ListingKind.Program,
        "event" => ListingKind.Event,
        "job" => ListingKind.Job,
        "resource" => ListingKind.Resource,
        _ => throw new InvalidOperationException($"Unknown listing kind '{value}' in the search read model."),
    };
}
