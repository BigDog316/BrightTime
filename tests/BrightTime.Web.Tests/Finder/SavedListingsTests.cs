using BrightTime.Web.Finder;
using Microsoft.AspNetCore.Http;

namespace BrightTime.Web.Tests.Finder;

public sealed class SavedListingsTests
{
    private static IReadOnlyList<Guid> Read(string? cookie)
    {
        DefaultHttpContext http = new();
        if (cookie is not null)
        {
            http.Request.Headers.Cookie = $"{SavedListings.CookieName}={cookie}";
        }

        return SavedListings.Read(http.Request);
    }

    [Fact]
    public void The_cookie_is_ids_only_and_bad_parts_are_dropped()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        Read($"{a:N}.not-an-id.{b:N}.{a:N}").ShouldBe([a, b]);
        Read(null).ShouldBeEmpty();
    }

    [Fact]
    public void An_oversized_cookie_is_ignored_rather_than_parsed()
    {
        string huge = string.Join('.', Enumerable.Range(0, SavedListings.MaxItems + 5).Select(_ => Guid.NewGuid().ToString("N")));

        Read(huge).ShouldBeEmpty();
    }

    [Fact]
    public void Writing_keeps_at_most_the_limit_and_is_http_only()
    {
        DefaultHttpContext http = new();
        Guid[] ids = [.. Enumerable.Range(0, SavedListings.MaxItems + 3).Select(_ => Guid.NewGuid())];

        SavedListings.Write(http.Response, ids, secure: true);

        string header = http.Response.Headers.SetCookie.ToString();
        header.ShouldContain("httponly", Case.Insensitive);
        header.ShouldContain("samesite=lax", Case.Insensitive);
        header.Split('=')[1].Split(';')[0].Split('.').Length.ShouldBe(SavedListings.MaxItems);
    }

    [Theory]
    [InlineData("/listings/robotics-club?lang=es", "/listings/robotics-club?lang=es")]
    [InlineData("https://evil.example/", "/saved")]
    [InlineData("//evil.example/", "/saved")]
    [InlineData("/\\evil.example", "/saved")]
    [InlineData(null, "/saved")]
    public void Coming_back_is_only_ever_to_this_site(string? asked, string expected) =>
        SavedListings.LocalPath(asked).ShouldBe(expected);
}
