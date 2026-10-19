using BrightTime.SharedKernel.Paging;

namespace BrightTime.SharedKernel.Tests;

public sealed class CursorTests
{
    [Fact]
    public void Round_trips()
    {
        var original = new Cursor("0.874210", Guid.Parse("0196c3e8-8f0a-7c2a-9b11-6f1d2e3a4b5c"));

        Cursor.TryDecode(original.Encode(), out Cursor decoded).ShouldBeTrue();
        decoded.ShouldBe(original);
    }

    [Fact]
    public void Round_trips_a_sort_value_containing_the_separator()
    {
        // The id is split off from the right, so a pipe inside the sort value
        // survives. Timestamps and scores do not contain one today; a future
        // composite sort key might.
        var original = new Cursor("a|b|c", Guid.CreateVersion7());

        Cursor.TryDecode(original.Encode(), out Cursor decoded).ShouldBeTrue();
        decoded.SortValue.ShouldBe("a|b|c");
        decoded.LastId.ShouldBe(original.LastId);
    }

    [Fact]
    public void Round_trips_the_instant_the_sort_was_measured_at()
    {
        var original = new Cursor("a|b", Guid.CreateVersion7(), Cursor.Precision(DateTimeOffset.UtcNow));

        Cursor.TryDecode(original.Encode(), out Cursor decoded).ShouldBeTrue();
        decoded.ShouldBe(original);
    }

    [Fact]
    public void A_cursor_from_before_the_instant_existed_still_decodes()
    {
        // Links already in people's history carry the two-part form.
        Guid id = Guid.Parse("0196c3e8-8f0a-7c2a-9b11-6f1d2e3a4b5c");
        string legacy = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes($"0.874210|{id:D}"));

        Cursor.TryDecode(legacy, out Cursor decoded).ShouldBeTrue();
        decoded.ShouldBe(new Cursor("0.874210", id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64-$$$")]
    [InlineData("bm8tc2VwYXJhdG9y")]
    public void A_malformed_cursor_is_rejected_rather_than_throwing(string? encoded)
    {
        // A bad cursor is a client problem. The caller falls back to page one;
        // it must not become a 500.
        Cursor.TryDecode(encoded, out Cursor cursor).ShouldBeFalse();
        cursor.ShouldBe(default);
    }

    [Fact]
    public void Encoded_cursors_are_url_safe()
    {
        // Base64Url, so a cursor survives a query string without escaping.
        string encoded = new Cursor("score=1.0/2+3", Guid.CreateVersion7()).Encode();

        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("=");
    }

    [Theory]
    [InlineData(0, PageSize.Default)]
    [InlineData(-5, PageSize.Default)]
    [InlineData(10, 10)]
    [InlineData(500, PageSize.Max)]
    public void Page_size_is_clamped(int requested, int expected) =>
        new PageSize(requested).Value.ShouldBe(expected);

    [Fact]
    public void Page_size_fetches_one_extra_row_to_detect_a_next_page() =>
        new PageSize(24).FetchCount.ShouldBe(25);
}
