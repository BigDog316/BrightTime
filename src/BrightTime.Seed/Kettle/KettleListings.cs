using System.Globalization;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Seed.Kettle;

/// <summary>
/// Places the activity templates at the approved organisations: 420 listings, each with a
/// schedule, price, audience, registration route and, where the template has them,
/// translations.
/// </summary>
/// <remarks>
/// Every choice is a function of a stable name (the organisation, the listing's ordinal) and
/// of <c>today</c>, never of iteration order or the clock, so a given day always produces the
/// same catalogue. Dates are relative to <c>today</c> so the site always has upcoming sessions.
/// </remarks>
internal static class KettleListings
{
    public const int Total = 420;

    public const string Zone = "America/New_York";

    private static readonly DayOfWeek[] Weekdays =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];

    public static IReadOnlyList<SeedListing> Build(
        IReadOnlyList<SeedOrganisation> organisations,
        IReadOnlyList<SeedSite> sites,
        IReadOnlyList<SeedArea> areas,
        DateOnly today)
    {
        SeedOrganisation[] approved = [.. organisations.Where(o => o.Status == "approved")];
        int[] quotas = Quotas(approved, sites);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        DateTimeOffset now = At(zone, today, new TimeOnly(12, 0));

        HashSet<string> slugs = new(StringComparer.OrdinalIgnoreCase);
        List<SeedListing> listings = [];

        for (int o = 0; o < approved.Length; o++)
        {
            SeedOrganisation org = approved[o];
            SeedSite[] orgSites = [.. sites.Where(s => s.OrgId == org.Id)];
            ListingTemplate[] candidates = Candidates(org);

            for (int n = 0; n < quotas[o]; n++)
            {
                ListingTemplate template = candidates[n % candidates.Length];
                SeedSite? site = template.Modality == "virtual" ? null : orgSites[n % orgSites.Length];
                listings.Add(Make(org, template, site, n, areas, today, now, zone, slugs));
            }
        }

        return listings;
    }

    /// <summary>Listings per organisation, in proportion to its size, summing to exactly <see cref="Total"/>.</summary>
    private static int[] Quotas(SeedOrganisation[] approved, IReadOnlyList<SeedSite> sites)
    {
        double[] weights = [.. approved.Select(o => 3.0 + (2.0 * sites.Count(s => s.OrgId == o.Id)))];
        double scale = Total / weights.Sum();
        int[] quotas = [.. weights.Select(w => (int)Math.Floor(w * scale))];

        // Largest remainder, ties broken by position, so the total is exact and stable.
        foreach (int i in Enumerable.Range(0, approved.Length)
                     .OrderByDescending(i => (weights[i] * scale) - quotas[i])
                     .ThenBy(i => i)
                     .Take(Total - quotas.Sum()))
        {
            quotas[i]++;
        }

        return quotas;
    }

    /// <summary>The templates an organisation offers, in a stable order of its own.</summary>
    private static ListingTemplate[] Candidates(SeedOrganisation org)
    {
        ListingTemplate[] inFocus = [.. KettleTemplates.All.Where(t => org.Focus.Contains(t.Category))];
        return [.. inFocus.OrderBy(t => SeedIds.Hash($"{org.Slug}/{t.Key}"))];
    }

    private static SeedListing Make(
        SeedOrganisation org,
        ListingTemplate template,
        SeedSite? site,
        int n,
        IReadOnlyList<SeedArea> areas,
        DateOnly today,
        DateTimeOffset now,
        TimeZoneInfo zone,
        HashSet<string> slugs)
    {
        string key = $"listing/{org.Slug}/{n}";
        Guid id = SeedIds.For(key);
        int h(string what) => SeedIds.Hash($"{key}/{what}");

        // Written in the organisation's first language when the template has text in it.
        string primary = org.LanguagesServed[0] is var first && template.Text.ContainsKey(first) ? first : "en";
        TemplateText text = template.Text[primary];

        string place = site is null ? "online" : areas.Single(a => a.Id == site.NeighbourhoodId).Name;
        string slug = UniqueSlug(Slug.From($"{text.Title} {place}"), slugs);

        IReadOnlyList<SeedSession> sessions = Sessions(template, site, key, today);
        SeedSession first0 = sessions[0];

        (string status, DateTimeOffset? published, DateTimeOffset? submitted, DateTimeOffset? unpublished) =
            Status(h("status") % 100, h("age"), now);

        (string costModel, int? min, int? max, string? note) = Cost(template.Cost, h("cost"));
        string registration = template.Registration switch
        {
            RegistrationPattern.External => "external",
            RegistrationPattern.Builtin => "builtin",
            RegistrationPattern.WaitlistOnly => "waitlist_only",
            _ => "none",
        };

        bool meals = template.Schedule is SchedulePattern.AfterSchool or SchedulePattern.BreakCamp
                     && org.Kind is "school" or "library" && h("meals") % 2 == 0;
        bool transport = org.Kind == "school" && template.Schedule == SchedulePattern.AfterSchool && h("bus") % 5 < 2;

        List<Guid> accommodations = [];
        if (site?.IsWheelchairAccessible == true)
        {
            accommodations.Add(KettleTaxonomy.TermId("accommodation", "wheelchair-accessible"));
        }

        foreach (string code in KettleTaxonomy.AccommodationCodes.Skip(1).Where(c => SeedIds.Hash($"{key}/accom/{c}") % 10 == 0))
        {
            accommodations.Add(KettleTaxonomy.TermId("accommodation", code));
        }

        DateOnly lastDay = sessions.Max(s => s.EndsOn ?? s.StartsOn);
        bool isEvent = template.Kind == "event";

        return new SeedListing
        {
            Id = id,
            OrgId = org.Id,
            Kind = template.Kind,
            Slug = slug,
            Title = text.Title,
            Summary = text.Summary,
            Description = text.Description,
            PrimaryLanguage = primary,
            Status = status,
            PublishedAt = published,
            SubmittedAt = submitted,
            UnpublishedAt = unpublished,

            // An event stops being listed the day after it ends.
            ExpiresAt = isEvent ? At(zone, lastDay.AddDays(1), new TimeOnly(0, 0)) : null,
            AgeMin = template.AgeMin,
            AgeMax = template.AgeMax,
            Modality = template.Modality,
            VirtualUrl = template.Modality == "virtual" ? $"https://example.org/kettle/{org.Slug}/online" : null,
            IsDropIn = template.Registration == RegistrationPattern.None && !isEvent,
            MealsProvided = meals,
            TransportProvided = transport,
            TransportNote = transport ? "A late bus runs to every neighborhood in the district." : null,
            CostModel = costModel,
            CostCentsMin = min,
            CostCentsMax = max,
            SlidingScaleNote = note,
            RegistrationMode = registration,
            ExternalUrl = registration == "external"
                ? $"https://example.org/kettle/{org.Slug}/{Uri.EscapeDataString(slug)}"
                : null,
            CapacityTotal = registration == "builtin" ? 12 + (h("capacity") % 29) : null,
            WaitlistEnabled = registration == "builtin",
            ContactEmail = org.PublicEmail,
            ContactPhone = org.PublicPhone,
            AccessibilityNote = site?.IsWheelchairAccessible == true && h("access-note") % 3 == 0
                ? "Step-free entrance and an accessible restroom."
                : null,
            SeasonLabel = template.Schedule is SchedulePattern.AfterSchool or SchedulePattern.Saturday or SchedulePattern.Shifts
                ? Season(first0.StartsOn)
                : null,
            SiteIds = site is null ? [] : [site.Id],
            TermIds =
            [
                KettleTaxonomy.TermId("category", template.Category),
                .. template.Interests.Select(i => KettleTaxonomy.TermId("interest", i)),
            ],
            AccommodationTermIds = accommodations,
            Languages = [.. new[] { primary }.Concat(org.LanguagesServed).Distinct(StringComparer.Ordinal)],
            Sessions = sessions,
            Deadlines = Deadlines(template, key, sessions, today, zone),
            Translations =
            [
                .. template.Text
                    .Where(t => t.Key != primary)
                    .Select(t => new SeedTranslation(t.Key, t.Value.Title, t.Value.Summary, t.Value.Description)),
            ],
        };
    }

    private static string UniqueSlug(string slug, HashSet<string> taken)
    {
        string candidate = slug;
        for (int i = 2; !taken.Add(candidate); i++)
        {
            candidate = $"{slug}-{i.ToString(CultureInfo.InvariantCulture)}";
        }

        return candidate;
    }

    /// <summary>About 93% published; the rest spread across the workflow so every state has rows.</summary>
    private static (string, DateTimeOffset?, DateTimeOffset?, DateTimeOffset?) Status(int roll, int ageHash, DateTimeOffset now)
    {
        DateTimeOffset published = now.AddDays(-5 - (ageHash % 145)).AddMinutes(-(ageHash % 600));
        return roll switch
        {
            < 3 => ("draft", null, null, null),
            < 5 => ("pending_review", null, now.AddDays(-2 - (ageHash % 8)), null),
            < 6 => ("changes_requested", null, now.AddDays(-6 - (ageHash % 8)), null),
            < 7 => ("unpublished", null, published.AddDays(-3), now.AddDays(-(ageHash % 20) - 1)),
            _ => ("published", published, published.AddDays(-3), null),
        };
    }

    private static (string Model, int? Min, int? Max, string? Note) Cost(CostPattern pattern, int hash) => pattern switch
    {
        // Fixed fees land on whole five dollars, from $25 to $300.
        CostPattern.Fixed => ("fixed", 2_500 + (hash % 56 * 500), null, null),
        CostPattern.SlidingScale => ("sliding_scale", hash % 5 * 500, 6_000 + (hash % 30 * 500),
            "Pay what you can on a sliding scale. No one is turned away for inability to pay."),
        CostPattern.Varies => ("varies", null, null, null),
        CostPattern.Stipend => ("stipend_paid", null, null, null),
        _ => ("free", null, null, null),
    };

    private static IReadOnlyList<SeedSession> Sessions(ListingTemplate template, SeedSite? site, string key, DateOnly today)
    {
        int h(string what) => SeedIds.Hash($"{key}/{what}");
        Guid sessionId = SeedIds.For(key + "/session");

        // Seasons: two thirds have started already, the rest start within three weeks.
        DateOnly seasonStart = h("season") % 3 == 0 ? today.AddDays(1 + (h("start") % 21)) : today.AddDays(-(h("start") % 40));
        DateOnly seasonEnd = seasonStart.AddDays(7 * (10 + (h("weeks") % 7)));

        switch (template.Schedule)
        {
            case SchedulePattern.AfterSchool:
                {
                    DayOfWeek a = Weekdays[h("day1") % Weekdays.Length];
                    DayOfWeek[] days = h("twice") % 2 == 0 ? [a] : [a, Weekdays[(Array.IndexOf(Weekdays, a) + 2) % Weekdays.Length]];
                    TimeOnly start = new TimeOnly(15, 0).AddMinutes(30 * (h("time") % 3));
                    TimeOnly end = start.AddMinutes(h("long") % 2 == 0 ? 90 : 120);
                    return [Weekly(sessionId, site, seasonStart, seasonEnd, start, end, days)];
                }

            case SchedulePattern.Saturday:
                {
                    TimeOnly start = h("time") % 2 == 0 ? new TimeOnly(10, 0) : new TimeOnly(9, 30);
                    return [Weekly(sessionId, site, seasonStart, seasonEnd, start, start.AddMinutes(120), [DayOfWeek.Saturday])];
                }

            case SchedulePattern.Shifts:
                {
                    DayOfWeek[] days = h("shift") % 2 == 0 ? [DayOfWeek.Tuesday, DayOfWeek.Thursday] : [DayOfWeek.Monday, DayOfWeek.Wednesday];
                    return [Weekly(sessionId, site, seasonStart, seasonEnd, new TimeOnly(16, 0), new TimeOnly(20, 0), days)];
                }

            case SchedulePattern.OpenHours:
                {
                    DayOfWeek[] days = h("open") % 2 == 0
                        ? [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday]
                        : [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday];
                    TimeOnly start = h("open-time") % 2 == 0 ? new TimeOnly(15, 0) : new TimeOnly(16, 0);
                    return [Weekly(sessionId, site, today.AddDays(-300), null, start, start.AddHours(4), days)];
                }

            case SchedulePattern.BreakCamp:
                {
                    // Two weeks of weekdays starting on a Monday two to three months out.
                    DateOnly monday = NextOrSame(today.AddDays(60 + (h("break") % 30)), DayOfWeek.Monday);
                    return [new SeedSession(sessionId, site?.Id, monday, monday.AddDays(11),
                        new TimeOnly(9, 0), new TimeOnly(15, 0), "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", [])];
                }

            case SchedulePattern.MultiDay:
                {
                    DateOnly friday = NextOrSame(today.AddDays(14 + (h("when") % 90)), DayOfWeek.Friday);
                    return [new SeedSession(sessionId, site?.Id, friday, friday.AddDays(2),
                        new TimeOnly(10, 0), new TimeOnly(18, 0), "FREQ=DAILY;COUNT=3", [])];
                }

            default:
                {
                    DateOnly day = today.AddDays(5 + (h("when") % 105));
                    bool weekend = h("weekend") % 2 == 0;
                    if (weekend)
                    {
                        day = NextOrSame(day, DayOfWeek.Saturday);
                    }
                    else if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    {
                        day = NextOrSame(day, DayOfWeek.Tuesday);
                    }

                    TimeOnly start = weekend ? new TimeOnly(11, 0) : new TimeOnly(18, 0);
                    return [new SeedSession(sessionId, site?.Id, day, day, start, start.AddHours(weekend ? 4 : 2), null, [])];
                }
        }
    }

    private static SeedSession Weekly(
        Guid id,
        SeedSite? site,
        DateOnly from,
        DateOnly? to,
        TimeOnly start,
        TimeOnly end,
        DayOfWeek[] days)
    {
        // Thanksgiving is the one closure every seeded program observes.
        List<DateOnly> closed = [];
        for (int year = from.Year; year <= (to ?? from.AddYears(1)).Year; year++)
        {
            DateOnly thanksgiving = Thanksgiving(year);
            if (thanksgiving >= from && (to is null || thanksgiving <= to) && days.Contains(thanksgiving.DayOfWeek))
            {
                closed.Add(thanksgiving);
            }
        }

        // DTSTART must itself match the rule, or RFC 5545 makes it an extra occurrence.
        DateOnly first = days.Select(d => NextOrSame(from, d)).Min();
        return new SeedSession(id, site?.Id, first, to, start, end,
            "FREQ=WEEKLY;BYDAY=" + string.Join(',', days.Select(ByDay)), closed);
    }

    private static IReadOnlyList<SeedDeadline> Deadlines(
        ListingTemplate template,
        string key,
        IReadOnlyList<SeedSession> sessions,
        DateOnly today,
        TimeZoneInfo zone)
    {
        if (template.DeadlineKind is not { } kind)
        {
            return [];
        }

        int hash = SeedIds.Hash(key + "/deadline");
        DateOnly start = sessions[0].StartsOn;
        DateOnly? due = kind switch
        {
            "registration" => start.AddDays(-2) > today.AddDays(3) ? start.AddDays(-2) : today.AddDays(5 + (hash % 15)),
            "audition" => today.AddDays(10 + (hash % 20)),
            "application" => today.AddDays(14 + (hash % 31)),
            "early_bird" => start.AddDays(-14) > today ? start.AddDays(-14) : null,
            "scholarship" => today.AddDays(30 + (hash % 30)),
            _ => null,
        };

        return due is { } d
            ? [new SeedDeadline(SeedIds.For(key + "/deadline/" + kind), kind, At(zone, d, new TimeOnly(23, 59)), null)]
            : [];
    }

    /// <summary>A local wall time in the tenant's zone, as an instant.</summary>
    internal static DateTimeOffset At(TimeZoneInfo zone, DateOnly day, TimeOnly time)
    {
        DateTime local = day.ToDateTime(time);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static DateOnly NextOrSame(DateOnly day, DayOfWeek target)
    {
        while (day.DayOfWeek != target)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    private static DateOnly Thanksgiving(int year) => NextOrSame(new DateOnly(year, 11, 1), DayOfWeek.Thursday).AddDays(21);

    private static string Season(DateOnly start) => start.Month switch
    {
        >= 9 => $"Fall {start.Year}",
        >= 6 => $"Summer {start.Year}",
        >= 3 => $"Spring {start.Year}",
        _ => $"Winter {start.Year}",
    };

    private static string ByDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "SU",
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        _ => "SA",
    };
}
