namespace BrightTime.Modules.Community.Contracts;

/// <summary>What a space is (<c>ck_space_kind</c>). An activity's own space is a cohort (D120).</summary>
public static class SpaceKinds
{
    public const string Cohort = "cohort";
}

/// <summary>A space's states (<c>ck_space_status</c>). An archived space reads and takes no posts.</summary>
public static class SpaceStatuses
{
    public const string Active = "active";
    public const string Archived = "archived";
}

/// <summary>Who someone is in a space (<c>ck_space_member_role</c>). Only a youth may be a minor.</summary>
public static class SpaceRoles
{
    /// <summary>A young person enrolled in the space's activity, who joins and leaves with the enrolment.</summary>
    public const string Youth = "youth";

    /// <summary>A member of the organisation's staff, added by an admin.</summary>
    public const string Staff = "staff";
}

/// <summary>A post's states (<c>ck_post_state</c>).</summary>
public static class PostStates
{
    public const string Published = "published";

    /// <summary>Held by screening for a moderator, and shown to no one but its author until released.</summary>
    public const string Held = "held";

    /// <summary>Taken down by a moderator; its author still sees it, marked so.</summary>
    public const string Removed = "removed";

    /// <summary>Taken down by its author; nobody sees it.</summary>
    public const string DeletedByAuthor = "deleted_by_author";
}
