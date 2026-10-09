using BrightTime.Infrastructure.Security;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Infrastructure.Tests;

public sealed class HmacBlindIndexerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TenantId TenantA = new(Guid.Parse("0196c3e8-0000-7000-8000-00000000000a"));
    private static readonly TenantId TenantB = new(Guid.Parse("0196c3e8-0000-7000-8000-00000000000b"));

    [Fact]
    public async Task Is_deterministic_so_a_lookup_can_find_the_row()
    {
        var indexer = new HmacBlindIndexer(new TestKeyProvider());

        byte[] first = await indexer.ComputeAsync(TenantA, "+19145550134", Ct);
        byte[] second = await indexer.ComputeAsync(TenantA, "+19145550134", Ct);

        first.ShouldBe(second);
    }

    [Theory]
    [InlineData("  +19145550134  ")]
    [InlineData("+19145550134")]
    public async Task Normalisation_matches_the_sql_side(string input)
    {
        // platform.blind_index does lower(btrim(value)). If the C# and the SQL
        // disagree by a single space, every lookup silently misses and the
        // application concludes the record does not exist.
        var indexer = new HmacBlindIndexer(new TestKeyProvider());

        byte[] computed = await indexer.ComputeAsync(TenantA, input, Ct);
        byte[] canonical = await indexer.ComputeAsync(TenantA, "+19145550134", Ct);

        computed.ShouldBe(canonical);
    }

    [Fact]
    public void Normalisation_is_trim_then_lowercase() =>
        HmacBlindIndexer.Normalise("  AB-12-CD  ").ShouldBe("ab-12-cd");

    [Fact]
    public async Task Two_tenants_produce_different_tokens_for_the_same_value()
    {
        // Otherwise a token becomes a cross-tenant join key: the same phone
        // number would be correlatable across regions.
        var indexer = new HmacBlindIndexer(new TestKeyProvider());

        byte[] a = await indexer.ComputeAsync(TenantA, "+19145550134", Ct);
        byte[] b = await indexer.ComputeAsync(TenantB, "+19145550134", Ct);

        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task Produces_a_full_sha256_token()
    {
        var indexer = new HmacBlindIndexer(new TestKeyProvider());

        (await indexer.ComputeAsync(TenantA, "x", Ct)).Length.ShouldBe(32);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_an_empty_value(string? value) =>
        await Should.ThrowAsync<ArgumentException>(
            async () => await new HmacBlindIndexer(new TestKeyProvider()).ComputeAsync(TenantA, value!, Ct));
}
