using BrightTime.Modules.Directory.Provider;

namespace BrightTime.Modules.Directory.Tests;

public sealed class ProviderFormsTests
{
    private static readonly Guid Hood = Guid.Parse("0196c3e8-0000-7000-8000-0000000000d1");

    private static OrganisationForm Organisation() => new()
    {
        DisplayName = "Kettle Chess Club",
        LegalName = "Kettle Chess Club Inc.",
        Kind = "nonprofit",
        WebsiteUrl = "https://example.org/chess",
        PublicEmail = "hello@example.org",
        Languages = ["en", "es"],
    };

    private static SiteForm Site() => new()
    {
        Name = "Main hall",
        AddressLine1 = "1 Board Street",
        Locality = "Kettleton",
        PostalCode = "10599",
        NeighbourhoodId = Hood.ToString(),
        Hours = [new SiteHoursForm("1", "15:00", "18:00", "school_year"), new SiteHoursForm("2", "", "", "all_year")],
    };

    [Fact]
    public void A_complete_organisation_passes() => ProviderForms.Check(Organisation()).ShouldBeEmpty();

    [Theory]
    [InlineData("WebsiteUrl", "provider.error.website")]
    [InlineData("Kind", "provider.error.kind")]
    [InlineData("PublicEmail", "editor.error.email")]
    [InlineData("DisplayName", "provider.error.required")]
    public void Each_organisation_field_is_checked(string field, string error)
    {
        OrganisationForm form = field switch
        {
            "WebsiteUrl" => Organisation() with { WebsiteUrl = "javascript:alert(1)" },
            "Kind" => Organisation() with { Kind = "cartel" },
            "PublicEmail" => Organisation() with { PublicEmail = "not an address" },
            _ => Organisation() with { DisplayName = "   " },
        };

        ProviderForms.Check(form)[field].Single().Key.ShouldBe(error);
    }

    [Fact]
    public void A_complete_site_passes_and_a_blank_hours_row_is_ignored() =>
        ProviderForms.Check(Site(), [Hood]).ShouldBeEmpty();

    [Fact]
    public void A_site_must_be_in_one_of_the_tenants_neighbourhoods() =>
        ProviderForms.Check(Site(), [Guid.NewGuid()])["NeighbourhoodId"].Single().Key.ShouldBe("provider.error.neighbourhood");

    [Theory]
    [InlineData("1", "18:00", "15:00", "school_year")]
    [InlineData("7", "15:00", "18:00", "school_year")]
    [InlineData("1", "3pm", "18:00", "school_year")]
    [InlineData("1", "15:00", "18:00", "monsoon")]
    public void Hours_that_cannot_be_read_are_refused(string day, string opens, string closes, string season) =>
        ProviderForms.Check(Site() with { Hours = [new SiteHoursForm(day, opens, closes, season)] }, [Hood])["Hours"].Single().Key.ShouldBe("provider.error.hours");

    [Theory]
    [InlineData("1059")]
    [InlineData("ABCDE")]
    public void A_zip_code_is_five_digits_or_zip_plus_four(string zip) =>
        ProviderForms.Check(Site() with { PostalCode = zip }, [Hood])["PostalCode"].Single().Key.ShouldBe("provider.error.postal_code");

    [Fact]
    public void Access_answers_are_yes_no_or_not_sure()
    {
        ProviderForms.TriState("yes").ShouldBe(true);
        ProviderForms.TriState("").ShouldBeNull();
        ProviderForms.Check(Site() with { Wheelchair = "maybe" }, [Hood])["Wheelchair"].Single().Key.ShouldBe("editor.error.choose_option");
    }
}
