using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.Modules.Opportunities.Search.Sample;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Paging;
using BrightTime.SharedKernel.Text;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class InMemoryListingFinderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 14, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<ListingSearchRow> Catalogue() => KettleCatalogue.Rows(new FixedClock(Now));

    private static InMemoryListingFinder Finder(IReadOnlyList<ListingSearchRow>? rows = null) =>
        new(rows ?? Catalogue(), new FixedClock(Now));

    private static SearchQuery Query(Action<SearchQueryEditor>? edit = null)
    {
        var editor = new SearchQueryEditor { TenantId = KettleCatalogue.TenantId };
        edit?.Invoke(editor);
        return editor.Build();
    }

    private static async Task<FinderPage> Run(SearchQuery query) =>
        await Finder().SearchAsync(query, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_browse_returns_every_published_listing_once()
    {
        FinderPage page = await Run(Query(q => q.PageSizeValue = 50));

        page.Items.Count.ShouldBe(Catalogue().Select(r => r.ListingId).Distinct().Count());
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Paging_returns_every_listing_once_however_long_the_family_takes()
    {
        // Relevance rises as a start approaches. Scored afresh on each page, rows drift past the
        // previous page's last score and are skipped or repeated, so each page must score at the
        // first page's instant. Two days pass between pages here.
        var finder = new InMemoryListingFinder(Catalogue(), new SteppingClock(Now, TimeSpan.FromDays(2)));
        SearchQuery query = Query(q => q.PageSizeValue = 3);

        List<Guid> seen = [];
        for (int pages = 0; pages < 100; pages++)
        {
            FinderPage page = await finder.SearchAsync(query, TestContext.Current.CancellationToken);
            seen.AddRange(page.Items.Select(i => i.ListingId.Value));
            if (page.NextCursor is null)
            {
                break;
            }

            Cursor.TryDecode(page.NextCursor, out Cursor next).ShouldBeTrue();
            query = query with { After = next };
        }

        seen.ShouldBeUnique();
        seen.Count.ShouldBe(Catalogue().Select(r => r.ListingId).Distinct().Count());
    }

    private sealed class SteppingClock(DateTimeOffset start, TimeSpan step) : IClock
    {
        private DateTimeOffset _next = start;

        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset now = _next;
                _next += step;
                return now;
            }
        }

        public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);

        public DateOnly TodayIn(TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(UtcNow, timeZone).DateTime);
    }

    [Fact]
    public async Task Unpublished_listings_are_never_returned_whatever_the_query_asks()
    {
        // The public finder must not show a draft, however the caller phrases it.
        List<ListingSearchRow> rows =
        [
            .. Catalogue(),
            .. Catalogue().Select(r => r with
            {
                ListingId = Guid.Parse("0196c3e8-0000-7000-8000-0000000fdead"),
                IsPublished = false,
                Title = "Secret draft",
            }),
        ];

        FinderPage page = await Finder(rows).SearchAsync(Query(q => { q.Text = "Secret"; q.PageSizeValue = 50; }),
            TestContext.Current.CancellationToken);

        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_query_for_another_tenant_sees_nothing()
    {
        FinderPage page = await Finder().SearchAsync(
            Query(q => q.TenantId = Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Free_only_returns_only_free_listings()
    {
        FinderPage page = await Run(Query(q => { q.FreeOnly = true; q.PageSizeValue = 50; }));

        page.Items.ShouldAllBe(i => i.IsFree);
        page.Items.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_maximum_price_excludes_pricier_listings()
    {
        FinderPage page = await Run(Query(q => { q.MaxCostCents = 5_000; q.PageSizeValue = 50; }));

        page.Items.ShouldAllBe(i => i.CostCents <= 5_000);
        page.Items.ShouldNotContain(i => i.Title == "Youth orchestra");
    }

    [Fact]
    public async Task An_age_filter_keeps_listings_whose_range_covers_the_age()
    {
        FinderPage page = await Run(Query(q =>
        {
            q.AllFacets = ["age:16"];
            q.PageSizeValue = 50;
        }));

        page.Items.ShouldContain(i => i.Title == "Summer camp counsellor (paid)");
        page.Items.ShouldNotContain(i => i.Title == "Youth orchestra", "its range stops at 14");
    }

    [Fact]
    public async Task A_kind_filter_keeps_only_that_kind()
    {
        FinderPage page = await Run(Query(q => { q.Kinds = ["job"]; q.PageSizeValue = 50; }));

        page.Items.ShouldAllBe(i => i.Kind == ListingKind.Job);
        page.Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Text_must_match_every_word()
    {
        FinderPage page = await Run(Query(q => { q.Text = "robotics club"; q.PageSizeValue = 50; }));

        page.Items.Select(i => i.Title).ShouldBe(["Robotics club"]);
    }

    [Fact]
    public async Task A_listing_matching_a_word_only_in_its_category_is_found_by_that_word()
    {
        // The fuzzy text includes category names, so a search for the category finds it.
        FinderPage page = await Run(Query(q => { q.Text = "STEM"; q.PageSizeValue = 50; }));

        page.Items.ShouldContain(i => i.Title == "Robotics club");
    }

    [Fact]
    public async Task Every_result_explains_its_ranking_in_plain_words()
    {
        FinderPage page = await Run(Query(q => { q.Text = "robotics"; q.PageSizeValue = 50; }));

        page.Items.ShouldAllBe(i => i.Reasons.Count > 0);
        page.Items[0].Reasons.ShouldContain(Phrase.Of("finder.reason.matches_search"));
    }

    [Fact]
    public async Task A_window_filter_keeps_exactly_the_listings_with_a_session_in_range()
    {
        DateTimeOffset from = new DateTimeOffset(Now.AddDays(7).Date, TimeSpan.Zero);
        DateTimeOffset to = from.AddDays(7);

        // The expected set is computed from the rows directly, so the test states the
        // rule rather than repeating the implementation.
        HashSet<Guid> expected = Catalogue()
            .Where(r => r.SessionWindowStart is not null && r.SessionWindowEnd is not null
                        && r.SessionWindowStart < to && r.SessionWindowEnd > from)
            .Select(r => r.ListingId)
            .ToHashSet();

        FinderPage page = await Run(Query(q =>
        {
            q.WindowStart = from;
            q.WindowEnd = to;
            q.PageSizeValue = 50;
        }));

        page.Items.Select(i => i.ListingId.Value).OrderBy(id => id).ShouldBe(expected.OrderBy(id => id).ToList());
        expected.ShouldNotBeEmpty("the sample has sessions in every week; an empty expectation would prove nothing");
    }

    [Theory]
    [InlineData(SearchSort.Relevance)]
    [InlineData(SearchSort.Soonest)]
    [InlineData(SearchSort.Newest)]
    [InlineData(SearchSort.Nearest)]
    public async Task Walking_every_page_returns_each_listing_exactly_once_for_every_sort(SearchSort sort)
    {
        // The property a keyset cursor exists to guarantee. Small pages make the
        // boundary between pages happen many times.
        List<Guid> seen = [];
        string? cursor = null;
        int pages = 0;

        do
        {
            SearchQuery query = Query(q =>
            {
                q.Sort = sort;
                q.PageSizeValue = 2;
                q.After = cursor is null ? null : Cursor.TryDecode(cursor, out Cursor c) ? c : null;
            });

            FinderPage page = await Run(query);
            seen.AddRange(page.Items.Select(i => i.ListingId.Value));
            cursor = page.NextCursor;
            pages++;
            pages.ShouldBeLessThan(20, "pagination must terminate");
        }
        while (cursor is not null);

        seen.Count.ShouldBe(seen.Distinct().Count(), "no listing may appear on two pages");
        seen.Count.ShouldBe(Catalogue().Select(r => r.ListingId).Distinct().Count(), "no listing may be skipped");
        pages.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task The_relevance_sort_puts_the_best_text_match_first()
    {
        FinderPage page = await Run(Query(q => { q.Text = "robotics"; q.Sort = SearchSort.Relevance; q.PageSizeValue = 50; }));

        page.Items[0].Title.ShouldBe("Robotics club");
    }

    [Fact]
    public async Task Soonest_orders_by_the_next_session()
    {
        FinderPage page = await Run(Query(q => { q.Sort = SearchSort.Soonest; q.PageSizeValue = 50; }));

        List<DateTimeOffset> starts = page.Items
            .Where(i => i.NextStartAt is not null)
            .Select(i => i.NextStartAt!.Value)
            .ToList();

        starts.ShouldBe(starts.OrderBy(s => s).ToList());
    }
}
