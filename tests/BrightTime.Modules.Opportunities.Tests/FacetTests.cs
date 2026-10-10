using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class FacetTests
{
    private static readonly Guid Org = Guid.Parse("0196c3e8-0000-7000-8000-0000000000aa");

    private static FacetInput Base(ListingKind kind = ListingKind.Program) => new()
    {
        Kind = kind,
        OrganisationId = Org,
    };

    private static IReadOnlyList<string> Tokens(FacetInput input) => ListingFacets.Compute(input);

    [Fact]
    public void A_listing_with_no_age_bounds_is_open_to_every_age()
    {
        // Without this, a search for age 6 would exclude every listing that never
        // stated a minimum, which is almost all of them.
        IReadOnlyList<string> tokens = Tokens(Base());

        for (int age = 0; age <= 30; age++)
        {
            tokens.ShouldContain($"age:{age}");
        }
    }

    [Fact]
    public void Age_tokens_cover_exactly_the_stated_range()
    {
        IReadOnlyList<string> tokens = Tokens(Base() with { AgeMin = 13, AgeMax = 15 });

        tokens.Where(t => t.StartsWith("age:", StringComparison.Ordinal))
            .ShouldBe(["age:13", "age:14", "age:15"], ignoreOrder: true);
    }

    [Fact]
    public void Grade_tokens_include_pre_k_as_minus_one()
    {
        IReadOnlyList<string> tokens = Tokens(Base() with { GradeMin = -1, GradeMax = 1 });

        tokens.ShouldContain("grade:-1");
        tokens.ShouldContain("grade:0");
        tokens.ShouldContain("grade:1");
        tokens.ShouldNotContain("grade:2");
    }

    [Fact]
    public void An_out_of_range_age_is_clamped_to_the_domain()
    {
        IReadOnlyList<string> tokens = Tokens(Base() with { AgeMin = -5, AgeMax = 99 });

        tokens.Count(t => t.StartsWith("age:", StringComparison.Ordinal)).ShouldBe(31);
    }

    [Fact]
    public void Free_is_a_bare_flag_and_only_for_a_free_listing()
    {
        Tokens(Base() with { CostModel = CostModel.Free }).ShouldContain("free");
        Tokens(Base() with { CostModel = CostModel.Fixed }).ShouldNotContain("free");
    }

    [Fact]
    public void Virtual_ok_is_set_for_virtual_and_hybrid_only()
    {
        Tokens(Base() with { Modality = Modality.Virtual }).ShouldContain("virtual_ok");
        Tokens(Base() with { Modality = Modality.Hybrid }).ShouldContain("virtual_ok");
        Tokens(Base() with { Modality = Modality.InPerson }).ShouldNotContain("virtual_ok");
    }

    [Fact]
    public void Taxonomy_codes_fold_case_and_spacing_so_one_filter_matches_both_spellings()
    {
        IReadOnlyList<string> tokens = Tokens(Base() with
        {
            Categories = ["After School", "after school"],
            Interests = ["  Robotics  "],
        });

        tokens.Where(t => t.StartsWith("cat:", StringComparison.Ordinal)).ShouldBe(["cat:afterschool"]);
        tokens.ShouldContain("int:robotics");
    }

    [Fact]
    public void Days_of_week_outside_zero_to_six_are_discarded()
    {
        IReadOnlyList<string> tokens = Tokens(Base() with { DaysOfWeek = [5, 5, 7, -1] });

        tokens.Where(t => t.StartsWith("dow:", StringComparison.Ordinal)).ShouldBe(["dow:5"]);
    }

    [Fact]
    public void Tokens_are_unique_and_sorted_so_the_array_is_stable()
    {
        IReadOnlyList<string> first = Tokens(Base() with { Categories = ["stem", "arts"] });
        IReadOnlyList<string> second = Tokens(Base() with { Categories = ["arts", "stem", "arts"] });

        first.ShouldBe(second);
        first.Distinct().Count().ShouldBe(first.Count);
        first.ShouldBe(first.OrderBy(t => t, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Sensitive_attributes_have_no_facet_token_at_all()
    {
        // Facets are filters, and the ranking uses a subset. Nothing about a
        // family's circumstances is a listing attribute, so no token can name one.
        IReadOnlyList<string> tokens = Tokens(Base() with
        {
            Accommodations = ["wheelchair"],
            Languages = ["es"],
            IsDropIn = true,
        });

        tokens.ShouldContain("accom:wheelchair");
        tokens.ShouldContain("lang:es");
        tokens.ShouldContain("dropin");
        tokens.ShouldNotContain(t => t.Contains("income", StringComparison.Ordinal)
                                     || t.Contains("race", StringComparison.Ordinal)
                                     || t.Contains("disab", StringComparison.Ordinal));
    }
}
