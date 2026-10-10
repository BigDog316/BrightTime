using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Domain;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class ListingValidatorTests
{
    private static ListingDraft Valid(ListingKind kind = ListingKind.Program) => new()
    {
        Kind = kind,
        Title = "Robotics club",
        Summary = "Build and program robots every Friday.",
        AgeMin = 11,
        AgeMax = 14,
        CostModel = CostModel.Free,
        RegistrationMode = RegistrationMode.External,
        ExternalUrl = "https://example.org/register",
        SiteCount = 1,
    };

    private static IReadOnlyDictionary<string, string[]> FieldErrors(ListingDraft draft)
    {
        Result result = ListingValidator.Validate(draft);
        result.IsSuccess.ShouldBeFalse();
        return result.Error!.Details!;
    }

    [Fact]
    public void A_complete_program_passes() =>
        ListingValidator.Validate(Valid()).IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_title_and_summary_are_required()
    {
        IReadOnlyDictionary<string, string[]> errors = FieldErrors(Valid() with { Title = "  ", Summary = null });

        errors.Keys.ShouldContain("title");
        errors.Keys.ShouldContain("summary");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public void An_age_outside_zero_to_thirty_is_rejected(int age) =>
        FieldErrors(Valid() with { AgeMin = age }).Keys.ShouldContain("ageMin");

    [Fact]
    public void A_maximum_age_below_the_minimum_is_rejected() =>
        FieldErrors(Valid() with { AgeMin = 14, AgeMax = 11 }).Keys.ShouldContain("ageMax");

    [Theory]
    [InlineData(-2)]
    [InlineData(13)]
    public void A_grade_outside_pre_k_to_twelve_is_rejected(int grade) =>
        FieldErrors(Valid() with { GradeMin = grade }).Keys.ShouldContain("gradeMin");

    [Fact]
    public void A_free_listing_cannot_carry_a_price() =>
        FieldErrors(Valid() with { CostModel = CostModel.Free, CostCentsMin = 500 })
            .Keys.ShouldContain("costCentsMin");

    [Fact]
    public void A_fixed_price_listing_needs_exactly_one_price()
    {
        FieldErrors(Valid() with { CostModel = CostModel.Fixed, CostCentsMin = null })
            .Keys.ShouldContain("costCentsMin");

        ListingValidator.Validate(Valid() with { CostModel = CostModel.Fixed, CostCentsMin = 2500 })
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_sliding_scale_needs_a_low_and_a_high_in_order()
    {
        FieldErrors(Valid() with { CostModel = CostModel.SlidingScale, CostCentsMin = 500, CostCentsMax = null })
            .Keys.ShouldContain("costCentsMin");

        FieldErrors(Valid() with { CostModel = CostModel.SlidingScale, CostCentsMin = 5000, CostCentsMax = 500 })
            .Keys.ShouldContain("costCentsMax");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ftp://example.org/x")]
    [InlineData("not a url")]
    public void External_registration_accepts_only_http_links(string url) =>
        FieldErrors(Valid() with { ExternalUrl = url }).Keys.ShouldContain("externalUrl");

    [Fact]
    public void External_registration_needs_a_link() =>
        FieldErrors(Valid() with { ExternalUrl = null }).Keys.ShouldContain("externalUrl");

    [Fact]
    public void Built_in_registration_needs_a_capacity()
    {
        ListingDraft builtIn = Valid() with { RegistrationMode = RegistrationMode.Builtin, ExternalUrl = null, CapacityTotal = null };

        FieldErrors(builtIn).Keys.ShouldContain("capacityTotal");
        ListingValidator.Validate(builtIn with { CapacityTotal = 20 }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_virtual_listing_needs_a_joinable_link_and_no_site()
    {
        ListingDraft virtualOnly = Valid() with { Modality = Modality.Virtual, SiteCount = 0, VirtualUrl = null };

        FieldErrors(virtualOnly).Keys.ShouldContain("virtualUrl");
        ListingValidator.Validate(virtualOnly with { VirtualUrl = "https://meet.example.org/abc" }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void An_in_person_listing_needs_at_least_one_site() =>
        FieldErrors(Valid() with { SiteCount = 0 }).Keys.ShouldContain("sites");

    [Fact]
    public void A_job_is_unpaid_or_stipended_and_never_priced_as_a_fee()
    {
        FieldErrors(Valid(ListingKind.Job) with { CostModel = CostModel.Fixed, CostCentsMin = 1000 })
            .Keys.ShouldContain("costModel");

        ListingValidator.Validate(Valid(ListingKind.Job) with { CostModel = CostModel.StipendPaid }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_resource_is_drop_in_and_has_no_registration() =>
        FieldErrors(Valid(ListingKind.Resource) with { RegistrationMode = RegistrationMode.External })
            .Keys.ShouldContain("registrationMode");

    [Theory]
    [InlineData("https://example.org", true)]
    [InlineData("http://example.org/a?b=c", true)]
    [InlineData("HTTPS://EXAMPLE.ORG", true)]
    [InlineData("javascript:void(0)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_absolute_http_urls_are_accepted(string? url, bool expected) =>
        ListingValidator.IsHttpUrl(url).ShouldBe(expected);
}
