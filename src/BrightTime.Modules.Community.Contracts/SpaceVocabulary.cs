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

/// <summary>A challenge's states (<c>ck_challenge_status</c>), as D122 uses them: open, then closed.</summary>
public static class ChallengeStatuses
{
    /// <summary>Taking work: its space's young people save and send theirs.</summary>
    public const string Open = "open";

    /// <summary>Closed by its staff: it reads as it was, takes no more work, and staff still give feedback.</summary>
    public const string Closed = "closed";
}

/// <summary>A submission's states (<c>ck_submission_state</c>), as D122 uses them.</summary>
public static class SubmissionStates
{
    /// <summary>Saved by its author and not yet sent; only they see it.</summary>
    public const string Draft = "draft";

    /// <summary>Sent, screened, and waiting for the space's staff.</summary>
    public const string Submitted = "submitted";

    /// <summary>Staff have commented without deciding.</summary>
    public const string FeedbackGiven = "feedback_given";

    /// <summary>Staff have accepted it.</summary>
    public const string Accepted = "accepted";

    /// <summary>Staff have sent it back: its author changes it and sends it again.</summary>
    public const string NeedsRework = "needs_rework";

    /// <summary>A moderator removed it, after a report (D128): it's on no showcase, and its author can't change it.</summary>
    public const string Removed = "removed";

    /// <summary>The states its author may change it in.</summary>
    public static bool Editable(string state) => state is Draft or NeedsRework;
}

/// <summary>What feedback decides (<c>ck_feedback_outcome</c>); none for a comment alone.</summary>
public static class FeedbackOutcomes
{
    public const string Accepted = "accepted";
    public const string NeedsRework = "needs_rework";
}
