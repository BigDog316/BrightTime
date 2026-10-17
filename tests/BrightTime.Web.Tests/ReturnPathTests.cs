using System.Security.Claims;
using BrightTime.Web.Auth;

namespace BrightTime.Web.Tests;

/// <summary>Where a person goes after signing in or confirming: their own site, always.</summary>
public sealed class ReturnPathTests
{
    private static readonly ClaimsPrincipal Provider = new(new ClaimsIdentity([], "test"));

    [Theory]
    [InlineData("/account/security", "/account/security")]
    [InlineData("/provider/orgs/abc?x=1", "/provider/orgs/abc?x=1")]
    [InlineData("//evil.example/path", "/provider")]
    [InlineData("/\\evil.example", "/provider")]
    [InlineData("https://evil.example/", "/provider")]
    [InlineData("javascript:alert(1)", "/provider")]
    [InlineData("/redirect?to=https://evil.example", "/provider")]
    [InlineData("", "/provider")]
    [InlineData(null, "/provider")]
    public void Only_a_local_path_is_followed(string? requested, string expected) =>
        ConsoleServices.LocalReturnPath(requested, Provider).ShouldBe(expected);
}
