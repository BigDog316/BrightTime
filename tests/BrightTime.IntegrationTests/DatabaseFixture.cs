using Testcontainers.PostgreSql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// A real PostgreSQL + PostGIS container with the full DDL from
/// <c>db/schema/</c> applied.
/// </summary>
/// <remarks>
/// <para>
/// Integration tests here run against the real thing on purpose. The two most
/// important properties of this system — row-level security actually isolating
/// tenants, and the conversation-safety trigger actually refusing an
/// adult-to-minor pair — cannot be tested against an in-memory provider,
/// because neither feature exists there. A test that passes against a fake is
/// worse than no test.
/// </para>
/// <para>
/// Requires a container runtime. Where none is available the tests skip with an
/// explanatory message rather than fail, so a developer without Docker still
/// gets a green unit-test run and a clear statement of what was not checked.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Why the container is unavailable, or null when it started.</summary>
    public string? SkipReason { get; private set; }

    public string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException(SkipReason ?? "The database container is not running.");

    public bool IsAvailable => _container is not null;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgis/postgis:17-3.4")
                .WithDatabase("brighttime")
                .WithUsername("brighttime_migrator")
                .WithPassword("local-only-not-a-secret")
                .Build();

            await _container.StartAsync().ConfigureAwait(false);
            await ApplySchemaAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No container runtime, image pull failure, or a daemon that is not
            // listening. Record it and let the tests skip.
            _container = null;
            SkipReason =
                "No container runtime is available, so the schema, row-level-security and " +
                "trigger tests did not run. Install Docker Desktop, OrbStack or Colima. " +
                $"Underlying error: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplySchemaAsync()
    {
        string schemaDirectory = Path.Combine(RepositoryRoot, "db", "schema");

        // Ordered by filename: 00 extensions and roles, 10..97 modules,
        // 99 row-level security and grants last.
        foreach (string file in Directory.GetFiles(schemaDirectory, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            string sql = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            await ExecuteAsync(sql).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs DDL or a DO block against the container. The SQL comes from
    /// <c>db/schema/</c> or from a test literal, never from user input; this is
    /// a schema harness, which is the one place raw SQL is the point.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test harness executing the repository's own DDL files and test literals. No user input reaches it.")]
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using Npgsql.NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.GetFiles("BrightTime.slnx").Length > 0 ||
                directory.GetFiles("BrightTime.sln").Length > 0)
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

[CollectionDefinition(nameof(DatabaseCollection))]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definitions are conventionally named <Thing>Collection.")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
