namespace BrightTime.Modules.Workforce.Contracts;

/// <summary>A course's states (<c>ck_course_status</c>), as D130 uses them.</summary>
public static class CourseStatuses
{
    /// <summary>Being written: only the county's organisers see it, and its units can change.</summary>
    public const string Draft = "draft";

    /// <summary>In the catalogue: adults who work with organisations take it, and its units are fixed.</summary>
    public const string Published = "published";

    /// <summary>Out of the catalogue. Completions stay on record; nothing can require it.</summary>
    public const string Retired = "retired";
}

/// <summary>How a course is delivered (<c>ck_course_delivery</c>).</summary>
public static class CourseDeliveries
{
    public const string SelfPaced = "self_paced";
    public const string LiveVirtual = "live_virtual";
    public const string InPerson = "in_person";
    public const string Blended = "blended";

    public static readonly string[] All = [SelfPaced, LiveVirtual, InPerson, Blended];
}

/// <summary>
/// Whom the county can require a course of (<c>ck_training_applies</c>), as D130 uses them: the
/// roles people hold at organisations. The schema's field collectors and kiosk operators wait for
/// those roles to take training.
/// </summary>
public static class TrainingRoles
{
    public const string Mentor = "mentor";
    public const string Staff = "staff";
    public const string OrgAdmin = "org_admin";

    public static readonly string[] All = [Mentor, Staff, OrgAdmin];
}

/// <summary>Where someone stands with a course (D130).</summary>
public static class TrainingStates
{
    public const string NotStarted = "not_started";
    public const string InProgress = "in_progress";

    /// <summary>Completed, and still counting.</summary>
    public const string Completed = "completed";

    /// <summary>Completed once, and past the date it counted until.</summary>
    public const string Expired = "expired";
}
