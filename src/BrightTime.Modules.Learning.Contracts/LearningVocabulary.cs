namespace BrightTime.Modules.Learning.Contracts;

/// <summary>
/// Who may see a young person's portfolio (<c>ck_learner_visibility</c>). Only three are offered
/// so far (D65); the schema also allows <c>mentors</c> and <c>org</c>, which wait for M5 and M6
/// to give them meaning.
/// </summary>
public static class PortfolioVisibility
{
    /// <summary>Only the young person.</summary>
    public const string Private = "private";

    /// <summary>Anyone with the link; never listed or indexed.</summary>
    public const string LinkOnly = "link_only";

    /// <summary>Anyone; may be listed and indexed.</summary>
    public const string Public = "public";

    public static IReadOnlySet<string> Offered { get; } = new HashSet<string>([Private, LinkOnly, Public], StringComparer.Ordinal);

    /// <summary>Reachable without signing in, so it needs the showcase capability and a slug.</summary>
    public static bool IsShown(string visibility) => visibility is LinkOnly or Public;
}

/// <summary>Where a participation record's hours came from (<c>ck_participation_source</c>).</summary>
public static class ParticipationSources
{
    public const string Attendance = "attendance";
    public const string ProviderEntry = "provider_entry";
    public const string SelfReported = "self_reported";
    public const string Import = "import";
    public const string DistrictFeed = "district_feed";
}
