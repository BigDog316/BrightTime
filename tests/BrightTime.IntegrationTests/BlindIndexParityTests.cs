using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The blind index must be byte-identical whether computed in C# or in PostgreSQL.
/// If they disagree, every lookup of an encrypted phone number silently misses.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BlindIndexParityTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("+19145550134", "+19145550134")]
    [InlineData("  +19145550134  ", "+19145550134")]
    [InlineData("  AB-12-CD  ", "AB-12-CD")]
    [InlineData("Marisol.Rivera@Example.org", "Marisol.Rivera@Example.org")]
    public async Task The_sql_and_csharp_tokens_match(string input, string expectedNormalised)
    {
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");
        ArgumentNullException.ThrowIfNull(expectedNormalised);

        byte[] key = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

        // What the application computes: HMAC over the normalised UTF-8 bytes.
        // The application's normaliser is trim then lower-case; the SQL does the same.
        byte[] fromCSharp = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(expectedNormalised.ToLowerInvariant()));

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
        byte[] fromSql = await Sql.ScalarAsync<byte[]>(connection, null,
            "select platform.blind_index(@value, @key)",
            [("value", input), ("key", key)]);

        fromSql.ShouldBe(fromCSharp, "the SQL and C# tokens differ; encrypted lookups would miss");
    }

    [Fact]
    public async Task A_different_key_produces_a_different_token()
    {
        // Otherwise the token is a bare hash, and anyone can dictionary-attack a phone number.
        Assert.SkipUnless(database.IsAvailable, database.SkipReason ?? "No database.");

        await using NpgsqlConnection connection = await Sql.OpenAsync(database.ConnectionString, Ct);
        byte[] keyA = Enumerable.Repeat((byte)1, 32).ToArray();
        byte[] keyB = Enumerable.Repeat((byte)2, 32).ToArray();

        byte[] a = await Sql.ScalarAsync<byte[]>(connection, null, "select platform.blind_index('x', @k)", [("k", keyA)]);
        byte[] b = await Sql.ScalarAsync<byte[]>(connection, null, "select platform.blind_index('x', @k)", [("k", keyB)]);

        a.ShouldNotBe(b);
    }
}
