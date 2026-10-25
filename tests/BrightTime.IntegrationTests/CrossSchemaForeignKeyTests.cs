using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// Holds the database to the module order (plan/01, D70). A foreign key may cross schemas only to
/// one of the shared reference tables, or into a module that the owning module may already
/// reference. A key pointing anywhere else welds two modules together the wrong way round.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CrossSchemaForeignKeyTests(DatabaseFixture database)
{
    /// <summary>plan/01's stable reference aggregates: any module may hold a key to these.</summary>
    private static readonly HashSet<string> SharedReferences = new(StringComparer.Ordinal)
    {
        "platform.tenant", "platform.neighbourhood", "platform.school", "platform.school_district",
        "platform.taxonomy_term", "platform.media_object", "identity.app_user", "directory.organisation", "directory.site",
    };

    /// <summary>plan/01's dependency order, by schema: each module's schema, and the schemas of the modules it may reference.</summary>
    private static readonly Dictionary<string, string[]> MayReference = new(StringComparer.Ordinal)
    {
        ["platform"] = [],
        ["identity"] = ["platform"],
        ["directory"] = ["platform", "identity"],
        ["opportunity"] = ["platform", "identity", "directory"],
        ["enrollment"] = ["platform", "identity", "directory", "opportunity"],
        ["attendance"] = ["platform", "identity", "directory", "opportunity", "enrollment"],
        ["learning"] = ["platform", "identity", "directory", "opportunity", "enrollment", "attendance"],
        ["community"] = ["platform", "identity", "directory", "opportunity", "learning"],
        ["workforce"] = ["platform", "identity", "directory", "opportunity", "learning"],
        ["landscape"] = ["platform", "identity", "directory", "opportunity"],
        ["analytics"] = ["platform", "identity"],
        ["notify"] = ["platform", "identity"],
        ["integration"] = ["platform", "identity", "directory", "opportunity", "enrollment", "analytics"],
    };

    [Fact]
    public async Task Cross_schema_foreign_keys_follow_the_module_order()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "Database unavailable.");
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, ct);
        await using NpgsqlCommand command = new("""
            select ns.nspname, cl.relname, con.conname, fns.nspname, fcl.relname
              from pg_constraint con
              join pg_class cl on cl.oid = con.conrelid
              join pg_namespace ns on ns.oid = cl.relnamespace
              join pg_class fcl on fcl.oid = con.confrelid
              join pg_namespace fns on fns.oid = fcl.relnamespace
             where con.contype = 'f' and ns.nspname <> fns.nspname and ns.nspname = any(@schemas)
             order by 1, 2, 3
            """, connection);
        command.Parameters.AddWithValue("schemas", MayReference.Keys.ToArray());

        List<string> wrongWay = [];
        int crossing = 0;
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                crossing++;
                string from = reader.GetString(0), table = reader.GetString(1), key = reader.GetString(2), to = reader.GetString(3), target = reader.GetString(4);
                if (!SharedReferences.Contains($"{to}.{target}") && !MayReference[from].Contains(to))
                {
                    wrongWay.Add($"{from}.{table} ({key}) -> {to}.{target}");
                }
            }
        }

        crossing.ShouldBeGreaterThan(0, "the query should see the schema's cross-schema keys");
        wrongWay.ShouldBeEmpty("a module's table may hold a key only to a shared reference table, or into a module it may reference");
    }
}
