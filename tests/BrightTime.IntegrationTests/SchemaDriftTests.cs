using System.Text;
using BrightTime.Infrastructure.Persistence;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Identity.Persistence;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Decision Q12: the SQL in db/schema is the source of truth and the EF model is
/// mapped to it. This fails on drift in either direction, for every table a module
/// context maps: a mapped column the database lacks, a database column the model
/// does not map, a type that differs, or nullability that differs.
/// </summary>
/// <remarks>
/// Tables no context maps yet are not checked. Modules arrive one at a time, and an
/// unmapped table is not drift. A table that is mapped must be mapped completely.
/// </remarks>
[Collection(nameof(DatabaseCollection))]
public sealed class SchemaDriftTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>System columns. Present on every table, absent from information_schema.</summary>
    private static readonly HashSet<string> SystemColumns = new(StringComparer.Ordinal)
    {
        "xmin", "xmax", "cmin", "cmax", "ctid", "tableoid",
    };

    public static TheoryData<string> Contexts() =>
    [
        nameof(PlatformDbContext),
        nameof(IdentityDbContext),
        nameof(DirectoryDbContext),
        nameof(OpportunitiesDbContext),
    ];

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task The_ef_model_matches_the_schema(string contextName)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await using BrightTimeDbContext context = CreateContext(contextName, database.ConnectionString);
        IModel model = context.GetService<IDesignTimeModel>().Model;

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);

        List<string> drift = [];

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            string schema = entity.GetSchema() ?? "public";
            if (table is null)
            {
                continue;
            }

            Dictionary<string, DbColumn> columns = await ReadColumnsAsync(connection, schema, table);
            if (columns.Count == 0)
            {
                drift.Add($"{schema}.{table}: mapped by {entity.ClrType.Name} but does not exist");
                continue;
            }

            StoreObjectIdentifier store = StoreObjectIdentifier.Table(table, schema);
            HashSet<string> mapped = new(StringComparer.Ordinal);

            foreach (IProperty property in entity.GetProperties())
            {
                string? column = property.GetColumnName(store);
                if (column is null || SystemColumns.Contains(column))
                {
                    continue;
                }

                mapped.Add(column);

                if (!columns.TryGetValue(column, out DbColumn? db))
                {
                    drift.Add($"{schema}.{table}.{column}: mapped ({entity.ClrType.Name}.{property.Name}) but not in the schema");
                    continue;
                }

                string efType = Normalise(property.GetColumnType(store));
                if (!string.Equals(efType, db.Type, StringComparison.Ordinal))
                {
                    drift.Add($"{schema}.{table}.{column}: model type '{efType}', schema type '{db.Type}'");
                }

                bool efNullable = property.IsColumnNullable(store);
                if (efNullable != db.Nullable)
                {
                    drift.Add($"{schema}.{table}.{column}: model {(efNullable ? "nullable" : "NOT NULL")}, " +
                              $"schema {(db.Nullable ? "nullable" : "NOT NULL")}");
                }
            }

            foreach (string unmapped in columns.Keys.Where(c => !mapped.Contains(c)).Order(StringComparer.Ordinal))
            {
                drift.Add($"{schema}.{table}.{unmapped}: in the schema but not mapped by {entity.ClrType.Name}");
            }
        }

        drift.ShouldBeEmpty(
            $"{contextName} has drifted from db/schema ({drift.Count}):" + Environment.NewLine +
            string.Join(Environment.NewLine, drift));
    }

    internal static BrightTimeDbContext CreateContext(string name, string connectionString)
    {
        var tenant = new TenantContext();

        return name switch
        {
            nameof(PlatformDbContext) => new PlatformDbContext(Options<PlatformDbContext>(connectionString), tenant),
            nameof(IdentityDbContext) => new IdentityDbContext(Options<IdentityDbContext>(connectionString), tenant),
            nameof(DirectoryDbContext) => new DirectoryDbContext(Options<DirectoryDbContext>(connectionString), tenant),
            nameof(OpportunitiesDbContext) => new OpportunitiesDbContext(Options<OpportunitiesDbContext>(connectionString), tenant),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown context."),
        };
    }

    private static DbContextOptions<T> Options<T>(string connectionString) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(connectionString, o => o.UseNetTopologySuite())
            .Options;

    private sealed record DbColumn(string Type, bool Nullable);

    private static async Task<Dictionary<string, DbColumn>> ReadColumnsAsync(
        NpgsqlConnection connection, string schema, string table)
    {
        await using NpgsqlCommand command = new(
            """
            select a.attname,
                   format_type(a.atttypid, a.atttypmod),
                   not a.attnotnull
            from pg_attribute a
            join pg_class c on c.oid = a.attrelid
            join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = @schema and c.relname = @table
              and a.attnum > 0 and not a.attisdropped
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter("schema", schema));
        command.Parameters.Add(new NpgsqlParameter("table", table));

        Dictionary<string, DbColumn> columns = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns[reader.GetString(0)] = new DbColumn(Normalise(reader.GetString(1)), reader.GetBoolean(2));
        }

        return columns;
    }

    /// <summary>
    /// Puts EF's store type and PostgreSQL's format_type() into one spelling. Only
    /// spelling differences are folded. A real type difference stays visible.
    /// </summary>
    internal static string Normalise(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "(none)";
        }

        string t = type.Trim().ToLowerInvariant();

        // PostGIS: EF writes "geography (Point, 4326)", format_type "geography(Point,4326)".
        var compact = new StringBuilder(t.Length);
        foreach (char ch in t)
        {
            if (ch != ' ' || !(compact.Length > 0 && (compact[^1] == '(' || compact[^1] == ',')))
            {
                compact.Append(ch);
            }
        }

        t = compact.ToString().Replace(" (", "(", StringComparison.Ordinal);

        // EF writes char(n), PostgreSQL character(n): the same type.
        if (t.StartsWith("char(", StringComparison.Ordinal))
        {
            t = "character" + t["char".Length..];
        }

        return t switch
        {
            "timestamptz" => "timestamp with time zone",
            "bool" => "boolean",
            "int2" => "smallint",
            "int4" => "integer",
            "int8" => "bigint",
            "float8" => "double precision",
            "float4" => "real",
            _ => t,
        };
    }
}
