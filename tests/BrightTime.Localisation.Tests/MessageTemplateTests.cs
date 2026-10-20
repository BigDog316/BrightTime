using System.Globalization;

namespace BrightTime.Localisation.Tests;

public sealed class MessageTemplateTests
{
    private static string Format(string template, string locale, params (string Name, object Value)[] args) =>
        MessageTemplate.Parse(template).Format(locale, args.ToDictionary(a => a.Name, a => a.Value),
            v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");

    [Fact]
    public void Arguments_are_filled_in() =>
        Format("From {organisation}", "en", ("organisation", "Kettle Code Club")).ShouldBe("From Kettle Code Club");

    [Theory]
    [InlineData(0, "No listings")]
    [InlineData(1, "1 listing")]
    [InlineData(2, "2 listings")]
    public void An_exact_match_wins_then_the_english_category(int n, string expected) =>
        Format("{count, plural, =0 {No listings} one {# listing} other {# listings}}", "en", ("count", n)).ShouldBe(expected);

    [Theory]
    [InlineData(0, "zero")]
    [InlineData(1, "one")]
    [InlineData(2, "two")]
    [InlineData(3, "few")]
    [InlineData(10, "few")]
    [InlineData(11, "many")]
    [InlineData(99, "many")]
    [InlineData(100, "other")]
    [InlineData(103, "few")]
    public void Arabic_uses_all_six_forms(int n, string expected) =>
        Format("{n, plural, zero {zero} one {one} two {two} few {few} many {many} other {other}}", "ar", ("n", n)).ShouldBe(expected);

    [Theory]
    [InlineData(0, "one")]
    [InlineData(1, "one")]
    [InlineData(2, "other")]
    public void Bengali_counts_zero_as_one(int n, string expected) =>
        Format("{n, plural, one {one} other {other}}", "bn", ("n", n)).ShouldBe(expected);

    [Fact]
    public void A_select_picks_by_word_and_falls_back_to_other()
    {
        const string t = "{by, select, provider {Only an organiser} other {Only the owner}}";
        Format(t, "en", ("by", "provider")).ShouldBe("Only an organiser");
        Format(t, "en", ("by", "organiser")).ShouldBe("Only the owner");
    }

    [Fact]
    public void Apostrophes_are_ordinary_characters() =>
        Format("You will register on {organisation}'s own website.", "en", ("organisation", "Library"))
            .ShouldBe("You will register on Library's own website.");

    [Theory]
    [InlineData("A } alone")]
    [InlineData("Never {closed")]
    [InlineData("{n, plural, one {x}}")]
    [InlineData("{n, plural, lots {x} other {y}}")]
    [InlineData("{n, choose, a {x} other {y}}")]
    public void A_malformed_string_is_refused(string text) =>
        MessageTemplate.TryParse(text, out _, out string? problem).ShouldBeFalse(problem);

    [Fact]
    public void A_template_reports_what_it_reads()
    {
        MessageTemplate t = MessageTemplate.Parse("{name} has {n, plural, one {# item} other {# items}}");

        t.Arguments.Keys.Order(StringComparer.Ordinal).ShouldBe(["n", "name"]);
        t.Arguments["n"].Kind.ShouldBe(ArgumentKind.Plural);
    }
}
