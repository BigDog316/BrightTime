using BrightTime.SharedKernel.Primitives;

namespace BrightTime.SharedKernel.Tests;

public sealed class LocaleTests
{
    [Fact]
    public void All_six_launch_locales_are_supported() =>
        Locale.Supported.Select(l => l.Code)
            .ShouldBe(["en", "es", "zh-Hans", "bn", "ht", "ar"]);

    [Fact]
    public void Arabic_is_the_only_right_to_left_locale() =>
        Locale.Supported.Where(l => l.IsRightToLeft).ShouldBe([Locale.Arabic]);

    [Fact]
    public void Only_english_and_spanish_have_a_stemmer()
    {
        // The honest limit from docs/plan/08-decisions.md D6: core PostgreSQL
        // ships Snowball stemmers for these two and nothing for the rest, so
        // the other four depend on the trigram index for recall.
        Locale.Supported.Where(l => !l.RequiresTrigramFallback)
            .ShouldBe([Locale.English, Locale.Spanish]);
    }

    [Theory]
    [InlineData("zh-Hans-CN", "zh-Hans")]
    [InlineData("en-GB", "en")]
    [InlineData("es-419", "es")]
    [InlineData("ar-EG", "ar")]
    public void Resolve_falls_back_along_the_tag(string requested, string expected) =>
        Locale.Resolve(requested).Code.ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("fr-CA")]
    [InlineData("klingon")]
    public void Resolve_falls_back_to_english(string? requested) =>
        Locale.Resolve(requested).ShouldBe(Locale.English);

    [Fact]
    public void Every_locale_has_a_usable_culture()
    {
        foreach (Locale locale in Locale.Supported)
        {
            locale.ToCulture().ShouldNotBeNull();
        }
    }
}
