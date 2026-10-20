using BrightTime.Hosting.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The shared key ring (D39): what one instance of a host protects, another opens, through
/// <c>platform.data_protection_key</c>; the rows hold no readable key; and the hosts may add keys
/// but never change or remove one.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DataProtectionKeysTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string VaultKey = Convert.ToBase64String(Enumerable.Range(7, 32).Select(i => (byte)i).ToArray());

    /// <summary>One instance of a host, as brighttime_app, with the vault key configured.</summary>
    private ServiceProvider Instance(string applicationName, string vaultKey) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(_ => NpgsqlDataSource.Create(TestHost.As(database.ConnectionString, TestHost.AppLogin)))
            .AddBrightTimeDataProtection(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Security:Keys:data-protection"] = vaultKey }).Build(),
                applicationName, development: false, hasDatabase: true)
            .BuildServiceProvider();

    [Fact]
    public async Task What_one_instance_protects_another_opens_and_the_table_holds_no_readable_key()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        string application = "BrightTime.Test." + Guid.NewGuid().ToString("N")[..8];

        await using ServiceProvider first = Instance(application, VaultKey);
        string sealedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("a passkey ceremony");

        await using ServiceProvider second = Instance(application, VaultKey);
        second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(sealedValue).ShouldBe("a passkey ceremony");

        // Another application's protector, on the same keys, cannot open it: purposes stay apart.
        await using ServiceProvider other = Instance(application + ".other", VaultKey);
        Should.Throw<System.Security.Cryptography.CryptographicException>(() => other.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(sealedValue));

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
        string xml = await Sql.ScalarAsync<string>(connection, null, "select xml from platform.data_protection_key order by id desc limit 1", []);
        xml.ShouldContain("vaultEncryptedKey");
        xml.ShouldNotContain("<masterKey", Case.Sensitive);
    }

    [Fact]
    public async Task The_hosts_may_add_keys_but_never_change_or_remove_one()
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        await using (ServiceProvider host = Instance("BrightTime.Test.append", VaultKey))
        {
            host.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("x");
        }

        await using NpgsqlConnection app = await Sql.OpenAsync(TestHost.As(database.ConnectionString, TestHost.AppLogin), Ct);
        foreach (string statement in new[] { "update platform.data_protection_key set xml = '<x/>'", "delete from platform.data_protection_key" })
        {
            PostgresException refused = await Should.ThrowAsync<PostgresException>(() => Sql.ExecAsync(app, null, statement, []));
            refused.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }
    }
}
