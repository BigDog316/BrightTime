using BrightTime.Web.Finder;
using static BrightTime.Web.Finder.PlainMarkdown;

namespace BrightTime.Web.Tests.Finder;

/// <summary>The organiser's Markdown: the few things it does, and the many it refuses to.</summary>
public sealed class PlainMarkdownTests
{
    [Fact]
    public void Paragraphs_headings_and_lists_are_read()
    {
        IReadOnlyList<Block> blocks = Parse("# About\n\nFirst line\nsame paragraph.\n\n- one\n- two");

        blocks.Count.ShouldBe(3);
        blocks[0].ShouldBeOfType<Heading>().Level.ShouldBe(3);
        blocks[1].ShouldBeOfType<Paragraph>().Runs.ShouldBe([new TextRun("First line same paragraph.")]);
        blocks[2].ShouldBeOfType<BulletList>().Items.Count.ShouldBe(2);
    }

    [Fact]
    public void Strong_emphasis_and_links_are_runs()
    {
        IReadOnlyList<Run> runs = Runs("**New?** Ask *staff* or see [the library](https://example.org/lib).");

        runs.ShouldBe(
        [
            new StrongRun("New?"),
            new TextRun(" Ask "),
            new EmphasisRun("staff"),
            new TextRun(" or see "),
            new LinkRun("the library", "https://example.org/lib"),
            new TextRun("."),
        ]);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("//evil.example/")]
    [InlineData("vbscript:msgbox")]
    public void A_link_to_anything_but_the_web_mail_phone_or_this_site_is_just_its_words(string href)
    {
        IReadOnlyList<Run> runs = Runs($"[click]({href})");

        runs.OfType<LinkRun>().ShouldBeEmpty();
        runs[0].ShouldBe(new TextRun("click"));
    }

    [Fact]
    public void Markup_in_the_source_stays_text_for_the_page_to_encode()
    {
        Runs("<script>alert(1)</script>").ShouldBe([new TextRun("<script>alert(1)</script>")]);
    }

    [Theory]
    [InlineData("/collections")]
    [InlineData("mailto:help@example.org")]
    [InlineData("tel:+15555550100")]
    public void Local_mail_and_phone_links_are_kept(string href) => IsSafe(href).ShouldBeTrue();

    [Fact]
    public void Nothing_in_is_nothing_out() => Parse("  \n\n ").ShouldBeEmpty();
}
