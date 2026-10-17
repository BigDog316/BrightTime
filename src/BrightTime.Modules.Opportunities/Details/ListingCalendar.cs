using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

namespace BrightTime.Modules.Opportunities.Details;

/// <summary>
/// The "add to calendar" file for a listing: one event per upcoming session.
/// </summary>
/// <remarks>
/// <para>
/// The sessions are written out one by one, in UTC, rather than as the session's
/// recurrence rule. The occurrences are what the projection already resolved (DST gaps,
/// cancellations and exceptions included), so the file says exactly what the page says,
/// and no calendar client has to re-run a rule it might interpret differently. The cost is
/// that the file is a snapshot of the next sessions the page lists, not the whole season.
/// </para>
/// <para>
/// The location is what the page shows and nothing more: a private site contributes only
/// its name and neighbourhood, never a street address.
/// </para>
/// </remarks>
public static class ListingCalendar
{
    public const string ContentType = "text/calendar; charset=utf-8";

    public static string ToIcs(ListingDetail listing, Uri pageUrl, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(pageUrl);

        var calendar = new Ical.Net.Calendar { ProductId = "-//BrightTime//Listing calendar//EN" };
        calendar.AddProperty("X-WR-CALNAME", listing.Title);

        string? location = Location(listing);
        string description = $"{listing.Summary}\n\n{listing.OrganisationName}\n{pageUrl}";

        foreach (ListingTime session in listing.Upcoming)
        {
            calendar.Events.Add(new CalendarEvent
            {
                // Stable per session, so re-importing updates an event instead of duplicating it.
                Uid = string.Create(CultureInfo.InvariantCulture,
                    $"{listing.Id.Value:N}-{session.StartsAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}@brighttime"),
                DtStamp = new CalDateTime(generatedAt.UtcDateTime),
                Start = new CalDateTime(session.StartsAt.UtcDateTime),
                End = new CalDateTime(session.EndsAt.UtcDateTime),
                Summary = listing.Title,
                Description = description,
                Location = location,
                Url = pageUrl,
            });
        }

        return new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("The calendar serializer returned nothing.");
    }

    /// <summary>The first place, as the page shows it. Online-only listings have none.</summary>
    internal static string? Location(ListingDetail listing)
    {
        if (listing.Places.Count == 0)
        {
            return listing.Modality == Modality.Virtual ? "Online" : null;
        }

        ListingPlace place = listing.Places[0];
        IEnumerable<string?> parts = place.IsAddressPublic
            ? [place.Name, place.AddressLine1, place.AddressLine2, place.Locality, place.PostalCode]
            : [place.Name, place.NeighbourhoodName];

        return string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
