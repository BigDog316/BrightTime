using BrightTime.Modules.Identity.SignIn;

namespace BrightTime.Modules.Identity.Tests;

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse kettle staple")]
    [InlineData("tr0mbone-lantern-orchard")]
    [InlineData("一二三四五六七八九十百千")]
    public void A_long_unpredictable_password_is_accepted(string password)
    {
        PasswordPolicy.Check(password, "maria@example.org").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("short")]
    [InlineData("elevenchars")]
    public void Fewer_than_twelve_characters_is_refused(string password)
    {
        PasswordPolicy.Check(password, null).ShouldContain(p => p.Contains("at least 12", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("password1234")]
    [InlineData("Password123!")]
    [InlineData("summer2026")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("abababababababab")]
    public void A_common_or_trivial_password_is_refused(string password)
    {
        PasswordPolicy.Check(password, null).ShouldContain(p => p.Contains("too common", StringComparison.Ordinal));
    }

    [Fact]
    public void A_password_built_from_the_email_is_refused()
    {
        PasswordPolicy.Check("mariagonzalez2026!", "mariagonzalez@example.org")
            .ShouldContain(p => p.Contains("email", StringComparison.Ordinal));
    }

    [Fact]
    public void There_are_no_composition_rules()
    {
        // All lower case, no digits, no symbols: NIST 800-63B says that is fine if it is long.
        PasswordPolicy.Check("lanternorchardriverbend", null).ShouldBeEmpty();
    }
}
