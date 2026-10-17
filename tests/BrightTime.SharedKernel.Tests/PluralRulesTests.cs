using BrightTime.SharedKernel.Text;

namespace BrightTime.SharedKernel.Tests;

/// <summary>CLDR cardinal rules for the six launch locales, at the numbers where they differ.</summary>
public sealed class PluralRulesTests
{
    [Theory]
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 0, PluralCategory.Other)]
    [InlineData("en", 2, PluralCategory.Other)]
    [InlineData("es", 1, PluralCategory.One)]
    [InlineData("es", 1_000_000, PluralCategory.Many)]
    [InlineData("es", 7, PluralCategory.Other)]
    [InlineData("bn", 0, PluralCategory.One)]
    [InlineData("bn", 1, PluralCategory.One)]
    [InlineData("bn", 2, PluralCategory.Other)]
    [InlineData("zh-Hans", 1, PluralCategory.Other)]
    [InlineData("ht", 1, PluralCategory.Other)]
    [InlineData("ar", 0, PluralCategory.Zero)]
    [InlineData("ar", 1, PluralCategory.One)]
    [InlineData("ar", 2, PluralCategory.Two)]
    [InlineData("ar", 5, PluralCategory.Few)]
    [InlineData("ar", 11, PluralCategory.Many)]
    [InlineData("ar", 100, PluralCategory.Other)]
    [InlineData("ar", 102, PluralCategory.Other)]
    [InlineData("ar", 111, PluralCategory.Many)]
    public void Whole_numbers(string locale, int n, PluralCategory expected) =>
        PluralRules.Select(locale, n).ShouldBe(expected);

    [Fact]
    public void A_fraction_is_not_one_in_english()
    {
        PluralRules.Select("en", 1.0m).ShouldBe(PluralCategory.Other, "\"1.0 km\" has a visible fraction");
        PluralRules.Select("en", 1.5m).ShouldBe(PluralCategory.Other);
        PluralRules.Select("ar", 2.5m).ShouldBe(PluralCategory.Other);
    }

    [Fact]
    public void Each_language_needs_the_forms_its_whole_numbers_reach()
    {
        PluralRules.Required("en").ShouldBe([PluralCategory.One, PluralCategory.Other]);
        PluralRules.Required("zh-Hans").ShouldBe([PluralCategory.Other]);
        PluralRules.Required("ar").Count.ShouldBe(6);
    }
}
