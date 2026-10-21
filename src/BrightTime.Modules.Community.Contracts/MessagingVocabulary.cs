namespace BrightTime.Modules.Community.Contracts;

/// <summary>What a conversation is (<c>ck_conversation_kind</c>). None of them is a pair.</summary>
public static class ConversationKinds
{
    public const string SpaceGroup = "space_group";
    public const string OrgToFamily = "org_to_family";
    public const string OrgBroadcast = "org_broadcast";
    public const string StaffInternal = "staff_internal";
    public const string MentorToOrg = "mentor_to_org";
}

/// <summary>Who someone is in a conversation (<c>ck_participant_role</c>). Only a youth may be a minor.</summary>
public static class ParticipantRoles
{
    public const string Youth = "youth";
    public const string Guardian = "guardian";
    public const string Staff = "staff";
    public const string Mentor = "mentor";

    /// <summary>An organisation admin who reads every thread with a minor, and whom the thread names as reading it.</summary>
    public const string OrgAdminObserver = "org_admin_observer";
}

/// <summary>A message's states (<c>ck_message_state</c>).</summary>
public static class MessageStates
{
    public const string Screening = "screening";
    public const string Sent = "sent";

    /// <summary>Held by screening for a moderator, and shown to no one but its sender until released.</summary>
    public const string Held = "held";

    public const string Removed = "removed";
}
