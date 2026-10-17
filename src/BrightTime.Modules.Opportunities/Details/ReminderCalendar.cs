using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace BrightTime.Modules.Opportunities.Details;

/// <summary>The words a reminder calendar needs, in the reader's language. The host supplies them.</summary>
/// <param name="CalendarName">The calendar's name in the reader's calendar app.</param>
/// <param name="DeadlineTitle">An event title for a deadline, given the listing and the deadline.</param>
/// <param name="SessionAlarm">The alarm text before a session.</param>
/// <param name="DeadlineAlarm">The alarm text before a deadline.</param>
public sealed record ReminderWords(
    string CalendarName,
    Func<ListingDetail, ListingDeadlineView, string> DeadlineTitle,
    string SessionAlarm,
    string DeadlineAlarm);

/// <summary>
/// Reminders without an account: one calendar file for the listings someone saved, with an
/// alarm an hour before each upcoming session and a day before each deadline.
/// </summary>
/// <remarks>
/// The calendar app on the person's own device does the reminding. BrightTime keeps no address,
/// phone number or schedule for them, which is the point while young people have no accounts
/// (decision D22). Locations follow <see cref="ListingCalendar"/>: never a private street address.
/// </remarks>
public static class ReminderCalendar
{
    public static readonly TimeSpan SessionLead = TimeSpan.FromHours(1);
    public static readonly TimeSpan DeadlineLead = TimeSpan.FromDays(1);

    public static string ToIcs(IEnumerable<ListingDetail> listings, Func<ListingDetail, Uri> pageUrl, ReminderWords words, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(pageUrl);
        ArgumentNullException.ThrowIfNull(words);

        var calendar = new Ical.Net.Calendar { ProductId = "-//BrightTime//Saved reminders//EN" };
        calendar.AddProperty("X-WR-CALNAME", words.CalendarName);

        foreach (ListingDetail listing in listings)
        {
            Uri url = pageUrl(listing);
            string? location = ListingCalendar.Location(listing);
            foreach (ListingTime session in listing.Upcoming)
            {
                calendar.Events.Add(Event(
                    string.Create(CultureInfo.InvariantCulture, $"{listing.Id.Value:N}-{session.StartsAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}@brighttime"),
                    listing.Title, session.StartsAt, session.EndsAt, location, url, generatedAt, SessionLead, words.SessionAlarm));
            }

            foreach (ListingDeadlineView deadline in listing.Deadlines.Where(d => d.DueAt > generatedAt))
            {
                calendar.Events.Add(Event(
                    string.Create(CultureInfo.InvariantCulture, $"{listing.Id.Value:N}-deadline-{deadline.Kind}@brighttime"),
                    words.DeadlineTitle(listing, deadline), deadline.DueAt, deadline.DueAt, null, url, generatedAt, DeadlineLead, words.DeadlineAlarm));
            }
        }

        return new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("The calendar serializer returned nothing.");
    }

    private static CalendarEvent Event(
        string uid, string summary, DateTimeOffset start, DateTimeOffset end, string? location, Uri url,
        DateTimeOffset generatedAt, TimeSpan lead, string alarm)
    {
        var e = new CalendarEvent
        {
            Uid = uid,
            DtStamp = new CalDateTime(generatedAt.UtcDateTime),
            Start = new CalDateTime(start.UtcDateTime),
            End = new CalDateTime(end.UtcDateTime),
            Summary = summary,
            Location = location,
            Url = url,
        };
        e.Alarms.Add(new Alarm
        {
            Action = AlarmAction.Display,
            Description = alarm,
            Trigger = new Trigger(Duration.FromTimeSpanExact(-lead)),
        });
        return e;
    }
}
