using System.Data;
using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using Npgsql;

namespace BrightTime.Modules.Opportunities.Search;

/// <summary>
/// The production finder: runs <see cref="SearchQueryBuilder"/>'s statement against
/// <c>opportunity.listing_search</c>.
/// </summary>
/// <remarks>
/// <para>
/// Decision D13. The query runs as <c>brighttime_finder</c>, a role that bypasses
/// row-level security and may read this one table and nothing else. Under the tenant
/// policy the planner could not use the search indexes; measured at 50,000 rows the
/// difference was 12–65 ms against 0.1–0.2 ms.
/// </para>
/// <para>
/// Isolation on this path therefore rests on the tenant predicate the builder always
/// emits. Two guards back it: this class refuses an empty tenant before connecting,
/// and the migration fails if <c>brighttime_finder</c> ever gains a privilege on any
/// other table. The role is entered with <c>SET LOCAL ROLE</c>, so the bypass lasts
/// for this transaction only and never for the connection.
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

        if (query.TenantId == Guid.Empty)
        {
            // Without RLS on this path, the tenant predicate is the isolation. An
            // empty tenant would match nothing today, but it must never reach SQL.
            throw new ArgumentException("A finder query must name its tenant.", nameof(query));
        }

        BuiltQuery built = SearchQueryBuilder.Build(query);

        await using NpgsqlConnection connection = await dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);

        await EnterFinderRoleAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

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

    /// <summary>The role that may bypass RLS on the search table, and only that table.</summary>
    public const string FinderRole = "brighttime_finder";

    /// <summary>
    /// Switches to the finder role for the rest of this transaction. <c>SET LOCAL</c>
    /// reverts at commit or rollback, so a pooled connection never carries the bypass
    /// into the next request.
    /// </summary>
    private static async Task EnterFinderRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new("set local role " + FinderRole, connection, transaction);
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
            reader.GetString(reader.GetOrdinal("slug")),
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
