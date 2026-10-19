using System.Text;
using System.Text.Json;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The finder's query plans as shipped: the SQL <see cref="SearchQueryBuilder"/> writes, run as
/// <c>brighttime_finder</c> exactly as <see cref="SqlListingFinder"/> runs it, over the synthetic
/// load (db/explain/synthetic-load.sql). This is the M1 gate: no sequential scan of the search
/// table, and the planner's row estimates on it within ten times of what it found.
/// </summary>
/// <remarks>
/// Set <c>BRIGHTTIME_EXPLAIN_OUT</c> to a directory to write each plan there (JSON and text),
/// which is how <c>db/explain/measured/shipped/</c> is refreshed.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class FinderPlanTests(DatabaseFixture database)
{
    public static readonly Guid SyntheticTenant = Guid.Parse("0196c3e8-0000-7000-8000-00000000e001");

    /// <summary>The load's version, carried in the synthetic tenant's display name.</summary>
    private const string LoadVersion = "Synthetic plans v4";

    /// <summary>Below this many rows on both sides, a misestimate is noise, not a finding.</summary>
    private const double EstimateFloor = 50;

    private const double EstimateTolerance = 10;

    private static readonly SemaphoreSlim LoadLock = new(1, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Coordinate School = new(41.1, -73.75);

    public static TheoryData<string> Scenarios() => [.. Queries.Keys];

    /// <summary>The seven scenarios of docs/plan/03, as the finder's own query objects.</summary>
    private static readonly Dictionary<string, Func<DateTimeOffset, (SearchQuery Query, bool Counts)>> Queries = new(StringComparer.Ordinal)
    {
        ["01-browse-soonest"] = _ => (Query() with { Sort = SearchSort.Soonest }, false),
        ["02-text-only"] = _ => (Query() with { Text = "robotics", TextSearchConfig = Locale.TextSearchEnglish }, false),
        ["03-facets-and-geo"] = _ => (Query() with
        {
            AllFacets = ["age:15", "cat:stem", "free"],
            Origin = School,
            Radius = new Distance(5_000),
            Sort = SearchSort.Nearest,
        }, false),
        ["04-date-window"] = now => (Query() with { WindowStart = now.AddDays(7), WindowEnd = now.AddDays(10), Sort = SearchSort.Soonest }, false),
        ["05-fuzzy"] = _ => (Query() with { Text = "robtics", TextSearchConfig = Locale.TextSearchEnglish }, false),
        ["06-everything"] = now => (Query() with
        {
            Text = "art",
            TextSearchConfig = Locale.TextSearchEnglish,
            AllFacets = ["age:12", "cat:arts", "dow:3"],
            Kinds = ["program"],
            WindowStart = now,
            WindowEnd = now.AddDays(30),
            MaxCostCents = 2_500,
            Origin = School,
            Radius = new Distance(10_000),
        }, false),
        // A filtered map. An unfiltered one counts the whole catalogue by neighbourhood, which is
        // a read of every row whatever the plan; on a one-tenant table that is rightly a scan.
        ["07-map-counts"] = _ => (Query() with { AllFacets = ["cat:stem", "age:12"] }, true),
    };

    private static SearchQuery Query() => new() { TenantId = SyntheticTenant, PageSize = new PageSize(PageSize.Default) };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_shipped_query_uses_an_index_and_estimates_its_rows(string scenario)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await EnsureLoadedAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        (SearchQuery query, bool counts) = Queries[scenario](now);
        BuiltQuery built = counts ? SearchQueryBuilder.BuildNeighbourhoodCounts(query) : SearchQueryBuilder.Build(query with { AsOf = now });

        string json = await ExplainAsync(built, "analyze, buffers, format json");
        JsonElement plan = JsonDocument.Parse(json).RootElement[0].GetProperty("Plan");
        List<JsonElement> nodes = [.. Walk(plan, underLimit: false, inBitmapSet: false).Select(n => n.Node)];

        if (Environment.GetEnvironmentVariable("BRIGHTTIME_EXPLAIN_OUT") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, scenario + ".json"), json, Ct);
            await File.WriteAllTextAsync(Path.Combine(outDir, scenario + ".txt"),
                built.Sql + "\n\n" + await ExplainAsync(built, "analyze, buffers"), Ct);
        }

        string[] seqScans = [.. nodes
            .Where(n => n.GetProperty("Node Type").GetString() == "Seq Scan" && Relation(n) == "listing_search")
            .Select(n => n.GetProperty("Node Type").GetString()!)];
        seqScans.ShouldBeEmpty($"{scenario} scans the whole search table:\n{Summary(nodes)}");

        // Judged: the scans that hand rows up, not each arm of a BitmapOr/BitmapAnd (the planner
        // acts on their combination, estimated at the heap scan above them), and not a scan a
        // LIMIT stopped early, whose estimate is for the whole scan by design.
        string[] misestimates = [.. Walk(plan, underLimit: false, inBitmapSet: false)
            .Where(w => !w.InBitmapSet && (Relation(w.Node) == "listing_search" || w.Node.TryGetProperty("Index Name", out _)))
            .Select(w => (w.Node, w.UnderLimit, Plan: w.Node.GetProperty("Plan Rows").GetDouble(), Actual: w.Node.GetProperty("Actual Rows").GetDouble()))
            .Where(x => !(x.UnderLimit && x.Actual <= x.Plan))
            .Select(x => (x.Node, x.Plan, x.Actual))
            .Where(x => Math.Max(x.Plan, x.Actual) >= EstimateFloor
                        && Math.Max(x.Plan, x.Actual) / Math.Max(Math.Min(x.Plan, x.Actual), 1) > EstimateTolerance)
            .Select(x => $"{x.Node.GetProperty("Node Type").GetString()} {Label(x.Node)}: estimated {x.Plan}, found {x.Actual}")];
        misestimates.ShouldBeEmpty($"{scenario} misestimates the search table:\n{Summary(nodes)}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The repository's own synthetic-load script.")]
    private async Task EnsureLoadedAsync()
    {
        await LoadLock.WaitAsync(Ct);
        try
        {
            await TestHost.EnsureLoginsAsync(database.ConnectionString, Ct);
            await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
            string? loaded = await Sql.ScalarAsync<string?>(connection, null,
                "select display_name from platform.tenant where id = @t", [("t", SyntheticTenant)]);
            if (loaded == LoadVersion)
            {
                return;
            }

            string script = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "db", "explain", "synthetic-load.sql"), Ct);
            await using NpgsqlCommand load = new(script, connection) { CommandTimeout = 300 };
            await load.ExecuteNonQueryAsync(Ct);
        }
        finally
        {
            LoadLock.Release();
        }
    }

    /// <summary>EXPLAIN of the built statement, run as the finder runs it: the finder login, then the finder role for the transaction.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The builder's own statement with fixed EXPLAIN options; values are bound parameters.")]
    private async Task<string> ExplainAsync(BuiltQuery built, string options)
    {
        await using NpgsqlConnection connection = await Sql.OpenAsync(TestHost.As(database.ConnectionString, TestHost.FinderLogin), Ct);
        await using NpgsqlTransaction tx = await connection.BeginTransactionAsync(Ct);
        await using (NpgsqlCommand role = new(SqlListingFinder.TransactionSetup, connection, tx))
        {
            await role.ExecuteNonQueryAsync(Ct);
        }

        await using NpgsqlCommand command = new($"explain ({options}) {built.Sql}", connection, tx);
        foreach ((string name, object? value) in built.Parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
        }

        StringBuilder result = new();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                result.AppendLine(reader.GetString(0));
            }
        }

        await tx.RollbackAsync(Ct);
        return result.ToString();
    }

    private static IEnumerable<(JsonElement Node, bool UnderLimit, bool InBitmapSet)> Walk(JsonElement node, bool underLimit, bool inBitmapSet)
    {
        yield return (node, underLimit, inBitmapSet);
        string type = node.GetProperty("Node Type").GetString()!;
        bool limit = underLimit || type == "Limit";
        bool set = type is "BitmapOr" or "BitmapAnd";
        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                foreach (var n in Walk(child, limit, set || (inBitmapSet && type.StartsWith("Bitmap", StringComparison.Ordinal))))
                {
                    yield return n;
                }
            }
        }
    }

    private static string? Relation(JsonElement node) =>
        node.TryGetProperty("Relation Name", out JsonElement r) ? r.GetString() : null;

    private static string Label(JsonElement node) =>
        node.TryGetProperty("Index Name", out JsonElement i) ? i.GetString()! : Relation(node) ?? "";

    private static string Summary(IEnumerable<JsonElement> nodes) =>
        string.Join('\n', nodes.Select(n =>
            $"  {n.GetProperty("Node Type").GetString()} {Label(n)} est={n.GetProperty("Plan Rows").GetDouble()} act={n.GetProperty("Actual Rows").GetDouble()} loops={n.GetProperty("Actual Loops").GetDouble()}"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BrightTime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Run from inside the repository.");
    }
}
