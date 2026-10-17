using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class ReminderCalendarTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static ListingDetail Listing(params ListingDeadlineView[] deadlines) => new()
    {
        Id = new ListingId(Guid.NewGuid()),
        Slug = "kite-club",
        Kind = ListingKind.Program,
        Title = "Kite club",
        Summary = "Build kites.",
        OrganisationName = "Kettle Youth Collective",
        CostModel = CostModel.Free,
        Modality = Modality.InPerson,
        RegistrationMode = RegistrationMode.None,
        Places = [new ListingPlace("Studio", false, null, null, null, null, "Westside", null, null)],
        Upcoming = [new ListingTime(Now.AddDays(2), Now.AddDays(2).AddHours(2))],
        Deadlines = deadlines,
    };

    private static string Ics(ListingDetail listing) => ReminderCalendar.ToIcs(
        [listing], l => new Uri("https://kettle.localhost/listings/" + l.Slug),
        new ReminderWords("Saved", (l, d) => $"Deadline: {l.Title}", "In an hour", "Tomorrow"), Now);

    [Fact]
    public void Each_session_reminds_an_hour_before_and_each_deadline_a_day_before()
    {
        string ics = Ics(Listing(new ListingDeadlineView("registration", Now.AddDays(5), null)));

        ics.ShouldContain("BEGIN:VALARM");
        ics.ShouldContain("TRIGGER:-PT1H");
        ics.ShouldContain("TRIGGER:-PT24H");
        ics.ShouldContain("SUMMARY:Deadline: Kite club");
        ics.ShouldContain("X-WR-CALNAME:Saved");
    }

    [Fact]
    public void A_deadline_already_past_is_left_out() =>
        Ics(Listing(new ListingDeadlineView("registration", Now.AddDays(-1), null))).ShouldNotContain("Deadline:");

    [Fact]
    public void A_private_site_contributes_its_neighbourhood_never_an_address() =>
        Ics(Listing()).ShouldContain("LOCATION:Studio\\, Westside");
}
