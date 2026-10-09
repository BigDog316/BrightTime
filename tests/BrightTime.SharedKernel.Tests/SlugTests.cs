using BrightTime.SharedKernel.Primitives;

namespace BrightTime.SharedKernel.Tests;

public sealed class SlugTests
{
    [Theory]
    [InlineData("Robotics Club", "robotics-club")]
    [InlineData("  After-School   STEM!  ", "after-school-stem")]
    [InlineData("Teatro Juvenil (Español)", "teatro-juvenil-espanol")]
    [InlineData("C++ for Teens", "c-for-teens")]
    [InlineData("2026 Summer Camp", "2026-summer-camp")]
    public void Latin_titles_slugify(string title, string expected) =>
        Slug.From(title).ShouldBe(expected);

    [Fact]
    public void Non_latin_titles_keep_their_letters()
    {
        // Four of six locales are non-Latin and transliteration is not
        // generally possible, so we keep the letters and let the URL be
        // percent-encoded rather than emit an empty or meaningless slug.
        Slug.From("机器人俱乐部").ShouldBe("机器人俱乐部");
        Slug.From("রোবোটিক্স ক্লাব").ShouldContain("ক্লাব");
    }

    [Fact]
    public void Punctuation_only_titles_fall_back() =>
        Slug.From("!!! ???", "listing").ShouldBe("listing");

    [Fact]
    public void A_completely_empty_input_still_yields_a_slug() =>
        Slug.From(null).ShouldBe("item");

    [Fact]
    public void Long_titles_are_truncated_without_a_trailing_separator()
    {
        string slug = Slug.From(string.Join(' ', Enumerable.Repeat("summer", 40)));

        slug.Length.ShouldBeLessThanOrEqualTo(Slug.MaxLength);
        slug.ShouldNotEndWith("-");
    }
}
