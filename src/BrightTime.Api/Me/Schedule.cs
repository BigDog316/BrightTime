using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Enrollment.Applications;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Personal;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;

namespace BrightTime.Api.Me;

/// <summary>
/// One meeting of an activity someone on the schedule is enrolled in. <c>Who</c> is their first
/// name; <c>Place</c> the first place the activity's page names, if any.
/// </summary>
[YouthData]
public sealed record ScheduleMeeting(Guid EnrollmentId, string Who, Guid ListingId, string ListingSlug, string ListingTitle, string? Place, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// A deadline of an activity the person saved (no <c>ApplicationId</c> or <c>Who</c>), or of one
/// with a draft application, named by the application and whom it is for.
/// </summary>
[YouthData]
public sealed record ScheduleDeadline(Guid ListingId, string ListingSlug, string ListingTitle, string Kind, string? Label, DateTimeOffset DueAt, Guid? ApplicationId, string? Who);

/// <summary>A place offered from a waitlist, waiting for the family's answer.</summary>
[YouthData]
public sealed record ScheduleOffer(Guid ApplicationId, string Who, Guid ListingId, string ListingTitle);

/// <summary>The schedule from <c>From</c> to <c>Until</c>: meetings and deadlines earliest first, and offers waiting.</summary>
[YouthData]
public sealed record ScheduleView(DateTimeOffset From, DateTimeOffset Until, IReadOnlyList<ScheduleMeeting> Meetings, IReadOnlyList<ScheduleDeadline> Deadlines, IReadOnlyList<ScheduleOffer> Offers);

/// <summary>
/// My schedule (D97, docs/plan/06): the next four weeks of meetings of every current enrolment the
/// person can see, as a household's owner or as the young person enrolled, the deadlines of what
/// they saved or began applying for, and offers waiting for an answer.
/// </summary>
/// <remarks>
/// The calendar file holds the same meetings and deadlines and no one's name: it goes into a phone's
/// calendar, which may be synced anywhere. It is made on request, never at a standing address, so
/// no link can show a stranger where a child will be.
/// </remarks>
public sealed class Schedule(
    IEnrolmentQueries enrolments, Modules.Enrollment.Applications.Applications applications, PersonalFinder personal, IListingOccurrences occurrences,
    IListingDetails details, ITenantContext tenant, IClock clock)
{
    public const string Read = "enrollment.schedule.read";

    /// <summary>How far ahead the schedule looks.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromDays(28);

    /// <summary>How far back a meeting may have begun and still be on.</summary>
    private static readonly TimeSpan Begun = TimeSpan.FromHours(12);

    /// <summary>The schedule, and the enrolments it read, for the audit.</summary>
    public Task<(ScheduleView View, IReadOnlyList<Guid> Enrolments)> GetAsync(UserId person, CancellationToken cancellationToken = default) =>
        BuildAsync(person, [], cancellationToken);

    private async Task<(ScheduleView View, IReadOnlyList<Guid> Enrolments)> BuildAsync(UserId person, Dictionary<Guid, ListingDetail?> found, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset until = now + Ahead;

        List<FamilyEnrolment> current = [.. (await enrolments.ForPersonAsync(person.Value, cancellationToken).ConfigureAwait(false))
            .Where(e => e.Status == EnrollmentStatuses.Active)];
        Dictionary<Guid, IReadOnlyList<ListingTime>> times = [];
        List<ScheduleMeeting> meetings = [];
        foreach (FamilyEnrolment e in current)
        {
            if (await DetailAsync(e.ListingId, found, cancellationToken).ConfigureAwait(false) is not { } listing)
            {
                continue;
            }

            if (!times.TryGetValue(e.ListingId, out IReadOnlyList<ListingTime>? those))
            {
                times[e.ListingId] = those = await occurrences.GetAsync(e.ListingId, now - Begun, until, cancellationToken).ConfigureAwait(false);
            }

            meetings.AddRange(those.Where(t => t.EndsAt >= now).Select(t =>
                new ScheduleMeeting(e.EnrollmentId, e.MemberFirstName, e.ListingId, listing.Slug, listing.Title, listing.Places.Count > 0 ? listing.Places[0].Name : null, t.StartsAt, t.EndsAt)));
        }

        IReadOnlyList<ApplicationSummary> applied = await applications.ListAsync(person, cancellationToken).ConfigureAwait(false);
        List<ScheduleOffer> offers = [.. applied.Where(a => a.Status == ApplicationStatuses.Offered)
            .Select(a => new ScheduleOffer(a.Id, a.MemberFirstName, a.ListingId, a.ListingTitle))];
        ILookup<Guid, ApplicationSummary> drafts = applied.Where(a => a.Status == ApplicationStatuses.Draft).ToLookup(a => a.ListingId);

        HashSet<Guid> saved = [.. (await personal.SavedAsync(person, cancellationToken).ConfigureAwait(false)).Select(s => s.ListingId)];
        List<Guid> watched = [.. saved.Union(drafts.Select(d => d.Key))];
        List<ScheduleDeadline> deadlines = [];
        foreach (ListingDeadlineDue d in await occurrences.DeadlinesAsync(watched, now, until, cancellationToken).ConfigureAwait(false))
        {
            if (await DetailAsync(d.ListingId, found, cancellationToken).ConfigureAwait(false) is not { } listing)
            {
                continue;
            }

            if (drafts.Contains(d.ListingId))
            {
                deadlines.AddRange(drafts[d.ListingId].Select(a => new ScheduleDeadline(d.ListingId, listing.Slug, listing.Title, d.Kind, d.Label, d.DueAt, a.Id, a.MemberFirstName)));
            }
            else
            {
                deadlines.Add(new ScheduleDeadline(d.ListingId, listing.Slug, listing.Title, d.Kind, d.Label, d.DueAt, null, null));
            }
        }

        ScheduleView view = new(now, until,
            [.. meetings.OrderBy(m => m.StartsAt).ThenBy(m => m.ListingTitle, StringComparer.CurrentCulture).ThenBy(m => m.Who, StringComparer.CurrentCulture)],
            [.. deadlines.OrderBy(d => d.DueAt).ThenBy(d => d.ListingTitle, StringComparer.CurrentCulture)],
            offers);
        return (view, [.. current.Select(e => e.EnrollmentId)]);
    }

    /// <summary>
    /// The schedule as a calendar file: each activity's meetings once, however many in the household
    /// go, an alarm an hour before, and each deadline with an alarm a day before. No names.
    /// </summary>
    public async Task<(string Ics, IReadOnlyList<Guid> Enrolments)> CalendarAsync(UserId person, Text text, Uri origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(origin);
        Dictionary<Guid, ListingDetail?> found = [];
        (ScheduleView view, IReadOnlyList<Guid> enrolled) = await BuildAsync(person, found, cancellationToken).ConfigureAwait(false);
        List<ListingDetail> listings = [];
        foreach (Guid id in view.Meetings.Select(m => m.ListingId).Concat(view.Deadlines.Select(d => d.ListingId)).Distinct())
        {
            if (found.GetValueOrDefault(id) is { } listing)
            {
                listings.Add(listing with
                {
                    Upcoming = [.. view.Meetings.Where(m => m.ListingId == id).Select(m => new ListingTime(m.StartsAt, m.EndsAt)).Distinct()],
                    Deadlines = [.. view.Deadlines.Where(d => d.ListingId == id).Select(d => new ListingDeadlineView(d.Kind, d.DueAt, d.Label)).Distinct()],
                });
            }
        }

        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        ReminderWords words = new(
            text["app.schedule.calendar_name"],
            (l, d) => text.Format("saved.calendar.deadline", ("title", l.Title),
                ("deadline", d.Label ?? (text.Has("listing.deadline." + d.Kind) ? text["listing.deadline." + d.Kind] : text["listing.deadline.other"]))),
            text["saved.calendar.session_alarm"],
            text["saved.calendar.deadline_alarm"]);
        string ics = ReminderCalendar.ToIcs(listings, l => new Uri(origin, $"/listings/{Uri.EscapeDataString(l.Slug)}"), words, view.From);
        return (ics, enrolled);
    }

    /// <summary>A listing as its page would show it, in the asker's language, looked up once.</summary>
    private async Task<ListingDetail?> DetailAsync(Guid listingId, Dictionary<Guid, ListingDetail?> found, CancellationToken ct)
    {
        if (!found.TryGetValue(listingId, out ListingDetail? listing))
        {
            found[listingId] = listing = await details.GetForReviewAsync(tenant.TenantId, listingId, tenant.Locale.Code, ct).ConfigureAwait(false);
        }

        return listing;
    }
}
