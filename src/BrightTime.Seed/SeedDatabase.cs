using BrightTime.Seed.Kettle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BrightTime.Seed;

/// <summary>
/// Builds a database from <c>db/schema</c> and seeds it: what <c>db/apply-schema.sh</c> plus
/// the seed do, without needing psql. Used by <c>--recreate</c> and by tests that need a
/// whole seeded tenant of their own.
/// </summary>
internal static class SeedDatabase
{
    /// <summary>
    /// Drops and recreates the database the connection names, applies every schema file in
    /// order, writes the seed and projects it. Refuses any database whose name does not start
    /// with "brighttime", so a wrong connection string cannot drop someone else's data.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The repository's own DDL files, and a database name checked against a fixed prefix and pattern.")]
    public static async Task<SeedData> RecreateAsync(
        string connection,
        string schemaDirectory,
        DateOnly today,
        ILogger? logger,
        CancellationToken ct)
    {
        var target = new NpgsqlConnectionStringBuilder(connection);
        string name = target.Database ?? throw new ArgumentException("The connection must name a database.", nameof(connection));
        if (!name.StartsWith("brighttime", StringComparison.Ordinal) || !name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new InvalidOperationException($"Refusing to drop '{name}': the name must start with 'brighttime' and use only a-z, 0-9 and _.");
        }

        var admin = new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false };
        await using (NpgsqlConnection server = new(admin.ConnectionString))
        {
            await server.OpenAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand drop = new($"drop database if exists {name} with (force)", server);
            await drop.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand create = new($"create database {name}", server);
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (NpgsqlConnection fresh = new(new NpgsqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString))
        {
            await fresh.OpenAsync(ct).ConfigureAwait(false);
            foreach (string file in Directory.GetFiles(schemaDirectory, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
            {
                await using NpgsqlCommand command = new(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), fresh);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        SeedData data = KettleSeed.Build(today);
        await using (NpgsqlDataSource source = NpgsqlDataSource.Create(connection))
        {
            await new SeedWriter(source).WriteAsync(data, ct).ConfigureAwait(false);
        }

        await SeedProjection.ProjectAsync(connection, data.TenantId, [.. data.Listings.Select(l => l.Id)], logger ?? NullLogger.Instance, ct)
            .ConfigureAwait(false);
        return data;
    }

    /// <summary><c>db/schema</c>, found by walking up from a starting directory.</summary>
    public static string FindSchemaDirectory(string start)
    {
        for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "db", "schema");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException($"No db/schema directory above {start}.");
    }
}
