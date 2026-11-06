namespace BrightTime.Modules.Notifications.Contracts;

/// <summary>The kinds of notification in <c>ck_notification_kind</c>, by their stored names.</summary>
public static class NotificationKinds
{
    public const string DeadlineReminder = "deadline_reminder";
    public const string SessionReminder = "session_reminder";
    public const string SavedItemUpdate = "saved_item_update";
    public const string ConsentRequest = "consent_request";

    /// <summary>A message in a conversation (D53). The push says only that there is one, and from whom.</summary>
    public const string NewMessage = "new_message";
    public const string System = "system";

    /// <summary>A young person's project or playlist step approved or sent back by staff (D83).</summary>
    public const string WorkReviewed = "work_reviewed";

    /// <summary>A badge awarded to a young person, by staff or for finishing a playlist (D83).</summary>
    public const string BadgeAwarded = "badge_awarded";

    /// <summary>
    /// The kinds a person can switch on or off. The rest arrive with the features that send them
    /// (applications, waitlist offers).
    /// </summary>
    public static readonly IReadOnlyList<string> UserConfigurable = [DeadlineReminder, SessionReminder, SavedItemUpdate, NewMessage, WorkReviewed, BadgeAwarded];
}

/// <summary>Delivery channels, as in <c>ck_preference_channel</c>.</summary>
public static class NotificationChannels
{
    public const string Push = "push";
    public const string Email = "email";
    public const string InApp = "in_app";

    /// <summary>SMS waits on Q9 (A2P registration); under-13 accounts never receive it.</summary>
    public const string Sms = "sms";

    /// <summary>The channels a person can choose in M2.</summary>
    public static readonly IReadOnlyList<string> UserConfigurable = [Push, Email, InApp];
}
