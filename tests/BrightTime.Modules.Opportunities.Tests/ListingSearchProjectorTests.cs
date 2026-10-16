using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Geo;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class ListingSearchProjectorTests
{
    private static readonly Guid Listing = Guid.Parse("0196c3e8-0000-7000-8000-000000000001");
    private static readonly Guid Org = Guid.Parse("0196c3e8-0000-7000-8000-000000000002");
    private static readonly Guid SiteA = Guid.Parse("0196c3e8-0000-7000-8000-00000000000a");
    private static readonly Guid SiteB = Guid.Parse("0196c3e8-0000-7000-8000-00000000000b");
    private static readonly Guid Hood = Guid.Parse("0196c3e8-0000-7000-8000-0000000000cc");

    private static ProjectionSource Source(IReadOnlyList<SiteSource> sites, bool published = true) => new()
    {
        ListingId = Listing,
        OrganisationId = Org,
        OrganisationName = "Kettle Youth Collective",
        IsPublished = published,
        PublishedAt = published ? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) : null,
        Facets = new FacetInput
        {
            Kind = ListingKind.Program,
            OrganisationId = Org,
            AgeMin = 11,
            AgeMax = 14,
            Categories = ["stem"],
        },
        Title = "Robotics club",
        Slug = "robotics-club",
        Summary = "Build robots every Friday.",
        CategoryNames = ["STEM"],
        Sites = sites,
        OccurrenceStarts = [new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero)],
        OccurrenceEnds = [new DateTimeOffset(2026, 10, 16, 2, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 9, 2, 30, 0, TimeSpan.Zero)],
        Deadlines = [new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)],
        CostCents = 0,
        IsFree = true,
    };

    [Fact]
    public void A_listing_at_two_sites_projects_two_rows_one_per_site()
    {
        var rows = ListingSearchProjector.Project(Source(
        [
            new SiteSource(SiteA, Hood, new Coordinate(41.03, -73.76)),
            new SiteSource(SiteB, null, new Coordinate(41.05, -73.80)),
        ]));

        rows.Count.ShouldBe(2);
        rows.Select(r => r.SiteId).ShouldBe([SiteA, SiteB], ignoreOrder: true);
    }

    [Fact]
    public void A_virtual_only_listing_projects_one_row_with_no_site()
    {
        var rows = ListingSearchProjector.Project(Source([]));

        rows.Count.ShouldBe(1);
        rows[0].SiteId.ShouldBeNull();
        rows[0].Location.ShouldBeNull();
    }

    [Fact]
    public void Every_row_carries_the_listing_publication_state()
    {
        ListingSearchProjector.Project(Source([], published: false))
            .ShouldAllBe(r => !r.IsPublished);

        ListingSearchProjector.Project(Source([], published: true))
            .ShouldAllBe(r => r.IsPublished);
    }

    [Fact]
    public void The_next_start_is_the_earliest_occurrence_and_the_window_spans_all_of_them()
    {
        ListingSearchRow row = ListingSearchProjector.Project(Source([])).Single();

        row.NextStartAt.ShouldBe(new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero));
        row.SessionWindowStart.ShouldBe(new DateTimeOffset(2026, 10, 9, 1, 0, 0, TimeSpan.Zero));
        row.SessionWindowEnd.ShouldBe(new DateTimeOffset(2026, 10, 16, 2, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_next_deadline_is_carried_through()
    {
        ListingSearchProjector.Project(Source([])).Single().NextDeadlineAt
            .ShouldBe(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_listing_with_no_occurrences_has_no_next_start_so_it_is_not_sorted_as_soonest()
    {
        ProjectionSource source = Source([]) with { OccurrenceStarts = [], OccurrenceEnds = [] };

        ListingSearchRow row = ListingSearchProjector.Project(source).Single();

        row.NextStartAt.ShouldBeNull();
        row.SessionWindowStart.ShouldBeNull();
    }

    [Fact]
    public void Facets_carry_the_neighbourhoods_of_every_site()
    {
        ListingSearchRow row = ListingSearchProjector.Project(Source(
        [
            new SiteSource(SiteA, Hood, new Coordinate(41.03, -73.76)),
        ])).Single();

        row.Facets.ShouldContain($"hood:{Hood:D}");
    }

    [Fact]
    public void The_fuzzy_text_includes_the_organisation_and_category_names_so_they_are_findable()
    {
        ListingSearchRow row = ListingSearchProjector.Project(Source([])).Single();

        row.FuzzyText.ShouldContain("Kettle Youth Collective");
        row.FuzzyText.ShouldContain("STEM");
        row.FuzzyText.ShouldContain("Robotics club");
    }

    [Fact]
    public void Popularity_is_carried_into_every_row_so_the_ranking_can_use_it()
    {
        // Missing from the first projector: the SQL ranks by popularity, so a row
        // without it would be ranked as if nothing had ever been saved or viewed.
        ProjectionSource source = Source([new SiteSource(SiteA, Hood, new Coordinate(41.03, -73.76))]) with { Popularity = 57.5 };

        ListingSearchProjector.Project(source).ShouldAllBe(r => r.Popularity == 57.5);
    }

    [Fact]
    public void Projection_is_deterministic_for_the_same_source()
    {
        // Compared field by field: records hold their collections by reference, so
        // two equal projections are not Equal as records. The projection job diffs
        // rows, so it must compare contents, not references.
        ProjectionSource source = Source([new SiteSource(SiteA, Hood, new Coordinate(41.03, -73.76))]);

        static string Fingerprint(IReadOnlyList<ListingSearchRow> rows) =>
            string.Join("|", rows.Select(r =>
                $"{r.ListingId}:{r.SiteId}:{string.Join(',', r.Facets)}:{r.NextStartAt:O}:{r.FuzzyText}"));

        Fingerprint(ListingSearchProjector.Project(source)).ShouldBe(Fingerprint(ListingSearchProjector.Project(source)));
    }
}
