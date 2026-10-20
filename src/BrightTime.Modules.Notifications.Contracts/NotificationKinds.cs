namespace BrightTime.Modules.Notifications.Contracts;

/// <summary>The kinds of notification in <c>ck_notification_kind</c>, by their stored names.</summary>
public static class NotificationKinds
{
    public const string DeadlineReminder = "deadline_reminder";
    public const string SessionReminder = "session_reminder";
    public const string SavedItemUpdate = "saved_item_update";
    public const string ConsentRequest = "consent_request";
    public const string System = "system";

    /// <summary>
    /// The kinds a person can switch on or off in M2. The rest arrive with the features that send
    /// them (applications, messaging, badges).
    /// </summary>
    public static readonly IReadOnlyList<string> UserConfigurable = [DeadlineReminder, SessionReminder, SavedItemUpdate];
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
