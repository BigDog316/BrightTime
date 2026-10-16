using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using Npgsql;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class SqlListingFinderGuardTests
{
    [Fact]
    public async Task An_empty_tenant_is_refused_before_any_connection_is_opened()
    {
        // The data source points nowhere. If the guard did not fire first, the test would
        // fail on the connection attempt instead of on the argument.
        await using NpgsqlDataSource nowhere = NpgsqlDataSource.Create("Host=invalid.invalid;Port=1;Timeout=1");
        var finder = new SqlListingFinder(nowhere);

        ArgumentException error = await Should.ThrowAsync<ArgumentException>(() =>
            finder.SearchAsync(new SearchQuery { TenantId = Guid.Empty }, TestContext.Current.CancellationToken));

        error.Message.ShouldContain("tenant");
    }

    [Fact]
    public void The_finder_role_name_matches_the_schema()
    {
        // 00-extensions-roles.sql creates this role. A typo here would fail every search
        // at run time with "role does not exist".
        string roles = File.ReadAllText(Path.Combine(RepositoryRoot(), "db", "schema", "00-extensions-roles.sql"));

        roles.ShouldContain($"create role {SqlListingFinder.FinderRole} nologin noinherit bypassrls;");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && directory.GetFiles("BrightTime.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
