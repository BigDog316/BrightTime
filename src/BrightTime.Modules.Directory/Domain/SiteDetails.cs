namespace BrightTime.Modules.Directory.Domain;

/// <summary>When a site is open. A season lets a school-year and a summer timetable sit side by side.</summary>
public sealed class SiteHours
{
    public Guid SiteId { get; init; }

    public short DayOfWeek { get; init; }

    public TimeOnly OpensAt { get; init; }

    public TimeOnly ClosesAt { get; init; }

    public string Season { get; init; } = SiteSeasons.AllYear;
}

public static class SiteSeasons
{
    public const string AllYear = "all_year";
    public const string SchoolYear = "school_year";
    public const string Summer = "summer";
    public const string WinterBreak = "winter_break";

    public static readonly string[] All = [AllYear, SchoolYear, Summer, WinterBreak];
}

/// <summary>Which sites a staff member works at.</summary>
public sealed class SiteAssignment
{
    public Guid OrgMembershipId { get; init; }

    public Guid SiteId { get; init; }
}
