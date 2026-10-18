using BrightTime.Seed;
using BrightTime.Seed.Kettle;
using BrightTime.SharedKernel.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// A database of its own built from <c>db/schema</c> and nothing else, with the Kettle seed
/// written and projected, as a deployment does it. Dropped and rebuilt at the start of a run,
/// not at the end, so a failure leaves it behind to inspect.
/// </summary>
internal static class KettleDatabase
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The repository's own DDL files and a constant database name.")]
    public static async Task<(string ConnectionString, SeedData Data, SeedCounts Counts)> CreateAsync(
        string superuserConnection, string databaseName, CancellationToken ct)
    {
        if (!databaseName.StartsWith("brighttime_", StringComparison.Ordinal) || !databaseName.All(c => char.IsAsciiLetterLower(c) || c == '_'))
        {
            throw new ArgumentException("Test databases are named brighttime_*.", nameof(databaseName));
        }

        await TestHost.EnsureLoginsAsync(superuserConnection, ct);

        await using (NpgsqlConnection admin = await Sql.OpenAsync(superuserConnection, ct))
        {
            await Sql.ExecAsync(admin, null, $"drop database if exists {databaseName} with (force)", []);
            await Sql.ExecAsync(admin, null, $"create database {databaseName}", []);
        }

        string connection = new NpgsqlConnectionStringBuilder(superuserConnection) { Database = databaseName }.ConnectionString;

        await using (NpgsqlConnection fresh = await Sql.OpenAsync(connection, ct))
        {
            string schema = Path.Combine(DatabaseFixture.RepositoryRoot, "db", "schema");
            foreach (string file in Directory.GetFiles(schema, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                await using NpgsqlCommand command = new(await File.ReadAllTextAsync(file, ct), fresh);
                await command.ExecuteNonQueryAsync(ct);
            }
        }

        SeedData data = KettleSeed.Build(SystemClock.Instance.TodayIn(TimeZoneInfo.FindSystemTimeZoneById(KettleListings.Zone)));

        await using NpgsqlDataSource source = NpgsqlDataSource.Create(connection);
        SeedCounts counts = await new SeedWriter(source).WriteAsync(data, ct);
        await SeedProjection.ProjectAsync(connection, data.TenantId, [.. data.Listings.Select(l => l.Id)], NullLogger.Instance, ct);
        return (connection, data, counts);
    }
}
