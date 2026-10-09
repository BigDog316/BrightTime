namespace BrightTime.Modules.Identity.Contracts;

/// <summary>
/// The things a guardian can consent to, one at a time.
/// </summary>
/// <remarks>
/// Scoped rather than a single blanket "yes". COPPA requires consent for the
/// collection it actually covers, and in practice a parent who is happy for
/// their child to enrol in a robotics club is not thereby happy for the
/// child's work to be published on a public showcase. Absence of a row means
/// not consented; there is no implied grant.
/// </remarks>
public enum ConsentScope
{
    /// <summary>Holding an account at all. Without this a child account cannot be active.</summary>
    Account,

    Enrollment,

    AttendancePhoto,

    CommunityParticipation,

    ShowcasePublication,

    MediaCapture,

    Messaging,

    DistrictDataShare,

    ResearchUse,

    LocationUse,
}

public static class ConsentScopeExtensions
{
    /// <summary>The database value, matching <c>ck_parental_consent_scope</c>.</summary>
    public static string ToDbValue(this ConsentScope scope) => scope switch
    {
        ConsentScope.Account => "account",
        ConsentScope.Enrollment => "enrollment",
        ConsentScope.AttendancePhoto => "attendance_photo",
        ConsentScope.CommunityParticipation => "community_participation",
        ConsentScope.ShowcasePublication => "showcase_publication",
        ConsentScope.MediaCapture => "media_capture",
        ConsentScope.Messaging => "messaging",
        ConsentScope.DistrictDataShare => "district_data_share",
        ConsentScope.ResearchUse => "research_use",
        ConsentScope.LocationUse => "location_use",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unmapped consent scope."),
    };

    public static bool TryFromDbValue(string? value, out ConsentScope scope)
    {
        foreach (ConsentScope candidate in Enum.GetValues<ConsentScope>())
        {
            if (string.Equals(candidate.ToDbValue(), value, StringComparison.Ordinal))
            {
                scope = candidate;
                return true;
            }
        }

        scope = default;
        return false;
    }
}
