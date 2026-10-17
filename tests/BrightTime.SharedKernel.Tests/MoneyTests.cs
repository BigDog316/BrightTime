using BrightTime.SharedKernel.Primitives;

namespace BrightTime.SharedKernel.Tests;

public sealed class MoneyTests
{
    public static TheoryData<string> Locales() => [.. Locale.Supported.Select(l => l.Code)];

    [Theory]
    [MemberData(nameof(Locales))]
    public void Dollars_carry_a_dollar_sign_in_every_supported_locale(string code)
    {
        Locale.TryFromCode(code, out Locale locale).ShouldBeTrue();

        string text = new Money(12_000).Format(locale);

        text.ShouldContain("$");
        text.ShouldNotContain("¤");
        text.ShouldContain("120");
    }

    [Fact]
    public void English_reads_the_way_a_New_Yorker_expects()
    {
        new Money(12_050).Format(Locale.English).ShouldBe("$120.50");
    }
}
