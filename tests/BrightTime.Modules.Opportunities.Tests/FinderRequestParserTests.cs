using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class FinderRequestParserTests
{
    private static readonly Guid Tenant = Guid.Parse("0196c3e8-0000-7000-8000-0000000000ee");

    private static Dictionary<string, string[]> Q(params (string Key, string Value)[] pairs) =>
        pairs.GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Value).ToArray(), StringComparer.Ordinal);

    private static FinderRequestParse Parse(params (string Key, string Value)[] pairs) =>
        FinderRequestParser.Parse(Q(pairs), Tenant, Locale.English);

    [Fact]
    public void An_empty_request_is_a_valid_browse_of_everything()
    {
        FinderRequestParse parse = Parse();

        parse.IsValid.ShouldBeTrue();
        parse.Query!.Sort.ShouldBe(SearchSort.Relevance);
        parse.Query.Text.ShouldBeNull();
        parse.Query.AllFacets.ShouldBeNull();
        parse.Query.Kinds.ShouldBeNull();
        parse.Query.TenantId.ShouldBe(Tenant);
    }

    [Fact]
    public void The_text_is_trimmed_and_blank_text_means_no_text_search()
    {
        Parse(("q", "  robotics  ")).Query!.Text.ShouldBe("robotics");
        Parse(("q", "   ")).Query!.Text.ShouldBeNull();
    }

    [Fact]
    public void Text_longer_than_the_limit_is_a_field_error()
    {
        FinderRequestParse parse = Parse(("q", new string('x', FinderRequestParser.MaxTextLength + 1)));

        parse.IsValid.ShouldBeFalse();
        parse.FieldErrors.ShouldContainKey("q");
    }

    [Fact]
    public void A_valid_age_becomes_an_age_facet()
    {
        Parse(("age", "15")).Query!.AllFacets!.ShouldBe(["age:15"]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("31")]
    [InlineData("fifteen")]
    [InlineData("15.5")]
    public void A_malformed_age_is_a_field_error_not_a_silent_drop(string age)
    {
        FinderRequestParse parse = Parse(("age", age));

        parse.IsValid.ShouldBeFalse();
        parse.FieldErrors.ShouldContainKey("age");
    }

    [Fact]
    public void Dollars_become_cents_and_are_bounded()
    {
        Parse(("maxDollars", "120")).Query!.MaxCostCents.ShouldBe(12_000);
        Parse(("maxDollars", "0")).Query!.MaxCostCents.ShouldBe(0);

        Parse(("maxDollars", "-5")).FieldErrors.ShouldContainKey("maxDollars");
        Parse(("maxDollars", "100001")).FieldErrors.ShouldContainKey("maxDollars");
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void Free_only_reads_common_checkbox_values(string value, bool expected) =>
        Parse(("free", value)).Query!.FreeOnly.ShouldBe(expected);

    [Fact]
    public void Known_kinds_are_kept_and_unknown_kinds_are_reported_not_silently_dropped()
    {
        FinderRequestParse parse = Parse(("kind", "program"), ("kind", "bogus"), ("kind", "JOB"));

        parse.Query!.Kinds!.ShouldBe(["program", "job"]);
        parse.Notices.ShouldContain(n => n.Contains("'bogus'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("soonest", SearchSort.Soonest)]
    [InlineData("NEWEST", SearchSort.Newest)]
    [InlineData("nearest", SearchSort.Nearest)]
    [InlineData("relevance", SearchSort.Relevance)]
    public void Sort_words_are_case_insensitive(string value, SearchSort expected) =>
        Parse(("sort", value)).Query!.Sort.ShouldBe(expected);

    [Fact]
    public void An_unknown_sort_is_a_field_error() =>
        Parse(("sort", "alphabetical")).FieldErrors.ShouldContainKey("sort");

    [Fact]
    public void A_stale_page_link_restarts_at_the_first_page_with_a_notice()
    {
        FinderRequestParse parse = Parse(("after", "not-a-real-cursor!!"));

        parse.IsValid.ShouldBeTrue();
        parse.Query!.After.ShouldBeNull();
        parse.Notices.ShouldContain(n => n.Contains("expired", StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_cursor_is_decoded()
    {
        var cursor = new Cursor("0.512300", Guid.CreateVersion7());

        Parse(("after", cursor.Encode())).Query!.After.ShouldBe(cursor);
    }

    [Fact]
    public void The_locale_chooses_the_text_search_configuration()
    {
        FinderRequestParser.Parse(Q(("q", "x")), Tenant, Locale.Spanish).Query!.TextSearchConfig
            .ShouldBe("platform.ts_es");
        FinderRequestParser.Parse(Q(("q", "x")), Tenant, Locale.Arabic).Query!.TextSearchConfig
            .ShouldBe(Locale.TextSearchSimple, "Arabic has no stemmer; it falls back to the simple configuration");
    }

    [Fact]
    public void Interest_tokens_come_from_the_caller_not_from_the_url()
    {
        // The family's declared interests are not something a URL should be able to
        // set, so the caller supplies them after reading the account.
        FinderRequestParser.Parse(Q(), Tenant, Locale.English, ["int:robotics"]).Query!.InterestTokens!
            .ShouldBe(["int:robotics"]);

        Parse(("int", "int:spoofed")).Query!.InterestTokens.ShouldBeNull();
    }

    [Fact]
    public void The_parser_reads_no_personal_value_it_was_not_given()
    {
        // The only inputs are search filters and the cursor. No address, school or
        // identity field is read from the URL, so none can reach the ranking.
        FinderRequestParse parse = Parse(("address", "12 Main St"), ("school", "Kettle High"), ("income", "low"));

        parse.IsValid.ShouldBeTrue();
        parse.Query!.Origin.ShouldBeNull();
        parse.Query.InterestTokens.ShouldBeNull();
    }
}
