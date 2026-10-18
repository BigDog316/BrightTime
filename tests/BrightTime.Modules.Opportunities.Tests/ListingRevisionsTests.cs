using BrightTime.Modules.Opportunities.Editing;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class ListingRevisionsTests
{
    private static readonly Guid Stem = Guid.Parse("0196c3e8-0000-7000-8000-0000000000c1");
    private static readonly Guid Arts = Guid.Parse("0196c3e8-0000-7000-8000-0000000000c2");

    private static ListingForm Form() => new()
    {
        Title = "Robotics club",
        Summary = "Build robots.",
        Description = "Line one\nLine two",
        TermIds = [Stem, Arts],
        Days = ["TU", "TH"],
        StartsOn = "2027-01-05",
        StartTime = "15:30",
        EndTime = "17:00",
        Version = "1234",
    };

    [Fact]
    public void A_snapshot_reads_back_as_the_same_form_without_its_row_version()
    {
        ListingForm original = Form();

        string json = ListingRevisions.Serialise(original);
        ListingForm back = ListingRevisions.Deserialise(json)!;

        original.Version.ShouldBe("1234", "serialising leaves the caller's form as it was");
        json.ShouldNotContain("1234");
        ListingRevisions.Compare(original, back).ShouldBeEmpty();
        back.TermIds.ShouldBe([Stem, Arts]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"format\": 99, \"form\": {}}")]
    [InlineData("not json")]
    public void A_snapshot_of_another_shape_is_not_read(string json) =>
        ListingRevisions.Deserialise(json).ShouldBeNull();

    [Fact]
    public void Changed_fields_are_reported_in_the_editors_order()
    {
        ListingForm after = Form();
        after.Title = "Robotics and coding club";
        after.MealsProvided = true;

        IReadOnlyList<FieldChange> changes = ListingRevisions.Compare(Form(), after);

        changes.Select(c => c.Field).ShouldBe([nameof(ListingForm.Title), nameof(ListingForm.MealsProvided)]);
        changes[0].Before.ShouldBe(["Robotics club"]);
        changes[0].After.ShouldBe(["Robotics and coding club"]);
        changes[1].After.ShouldBe(["true"]);
    }

    [Fact]
    public void Lists_compare_as_sets_and_text_ignores_line_endings_and_edges()
    {
        ListingForm after = new()
        {
            Title = "  Robotics club ",
            Summary = "Build robots.",
            Description = "Line one\r\nLine two",
            TermIds = [Arts, Stem],
            Days = ["TH", "TU", "TU"],
            StartsOn = "2027-01-05",
            StartTime = "15:30",
            EndTime = "17:00",
        };

        ListingRevisions.Compare(Form(), after).ShouldBeEmpty();
    }

    [Fact]
    public void A_list_change_holds_both_sets_sorted()
    {
        ListingForm after = Form();
        after.Days.Remove("TH");
        after.Days.Add("SA");

        FieldChange days = ListingRevisions.Compare(Form(), after).Single();

        days.IsList.ShouldBeTrue();
        days.Before.ShouldBe(["TH", "TU"]);
        days.After.ShouldBe(["SA", "TU"]);
    }
}
