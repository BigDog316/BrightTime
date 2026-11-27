using BrightTime.Abstractions.Screening;
using BrightTime.Modules.Community.Screening;

namespace BrightTime.IntegrationTests;

/// <summary>
/// The local screen for what young people write (D117): it finds a phone number, an email address
/// (spelt out too), a street address, a social handle and a link, each with its place in the
/// text; it holds on any of them and passes the rest; a year range isn't a phone number.
/// </summary>
public sealed class LocalPiiScreenTests
{
    private static readonly ScreeningContext Youth = new(AuthorIsMinor: true, AudienceIncludesMinor: true, AuthorIsAdultAddressingMinor: false, Surface: "message");

    [Theory]
    [InlineData("Call me on 914-555-0103 after school", "phone", "914-555-0103")]
    [InlineData("my cell is (914) 555 0103", "phone", "(914) 555 0103")]
    [InlineData("email kofi.mensah@example.com", "email", "kofi.mensah@example.com")]
    [InlineData("kofi (at) example (dot) com", "email", "kofi (at) example (dot) com")]
    [InlineData("I live at 12 Elm Street, come by", "address", "12 Elm Street")]
    [InlineData("we're at 4 on Main St.", "address", "4 on Main St.")]
    [InlineData("follow me @kofi_15 ok", "handle", "@kofi_15")]
    [InlineData("look at www.example.org/me", "link", "www.example.org/me")]
    public void Each_kind_is_found_where_it_is(string text, string kind, string found)
    {
        ArgumentNullException.ThrowIfNull(text);
        PiiSpan span = LocalPiiScreen.Find(text).ShouldHaveSingleItem();
        span.Kind.ShouldBe(kind);
        text.Substring(span.Start, span.Length).ShouldBe(found);
    }

    [Theory]
    [InlineData("See you Tuesday at 4!")]
    [InlineData("The 2025-2026 season starts soon")]
    [InlineData("I scored 12 points and we won 3-1")]
    [InlineData("Is the room on the second floor?")]
    public void Ordinary_writing_passes(string text) => LocalPiiScreen.Find(text).ShouldBeEmpty();

    [Fact]
    public async Task Personal_information_holds_with_its_kinds_and_spans_and_the_rest_passes()
    {
        LocalPiiScreen screen = new();
        ScreeningOutcome held = await screen.ScreenTextAsync("Text 914-555-0103 or mail me at a@b.org", Youth, TestContext.Current.CancellationToken);
        held.Decision.ShouldBe(ScreeningDecision.Hold);
        held.Labels.Keys.ShouldBe(["phone", "email"], ignoreOrder: true);
        held.PiiSpans.Count.ShouldBe(2, "the email isn't counted again as a link");
        held.ModelVersion.ShouldBe(LocalPiiScreen.Version);

        ScreeningOutcome passed = await screen.ScreenTextAsync("Thanks, see you there.", Youth, TestContext.Current.CancellationToken);
        passed.Decision.ShouldBe(ScreeningDecision.Pass);
        passed.PiiSpans.ShouldBeEmpty();
    }
}
