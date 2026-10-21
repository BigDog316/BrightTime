using System.Text.RegularExpressions;

namespace BrightTime.Modules.Community.Messaging;

/// <summary>
/// Screens an adult's message to a young person for an attempt to take the conversation off the
/// platform (docs/plan/07, T1): a phone number, an email address, a link, a social handle, the
/// name of another messaging app, or a request to be contacted privately.
/// </summary>
/// <remarks>
/// A flagged message is held for a moderator, never silently dropped: a false positive costs a
/// short delay, a miss is the threat this exists for. It errs towards holding.
/// </remarks>
public static partial class ContactExchangeScreen
{
    public const string Phone = "phone";
    public const string Email = "email";
    public const string Link = "link";
    public const string Handle = "handle";
    public const string OtherApp = "other_app";
    public const string PrivateContact = "private_contact";

    /// <summary>Why the message would be held, or nothing when it may go.</summary>
    public static IReadOnlyList<string> Reasons(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        List<string> reasons = [];
        if (PhoneNumber().Matches(body).Any(m => m.Value.Count(char.IsAsciiDigit) >= 7))
        {
            reasons.Add(Phone);
        }

        if (EmailAddress().IsMatch(body))
        {
            reasons.Add(Email);
        }

        if (Url().IsMatch(body))
        {
            reasons.Add(Link);
        }

        if (SocialHandle().IsMatch(body))
        {
            reasons.Add(Handle);
        }

        if (MessagingApp().IsMatch(body))
        {
            reasons.Add(OtherApp);
        }

        if (PrivateRequest().IsMatch(body))
        {
            reasons.Add(PrivateContact);
        }

        return reasons;
    }

    [GeneratedRegex(@"\+?\d[\d\s().\-]{5,}\d")]
    private static partial Regex PhoneNumber();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+\s*(@|\(at\)|\[at\])\s*[A-Za-z0-9.\-]+\s*(\.|\(dot\)|\[dot\])\s*[A-Za-z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(https?://|www\.)\S+|\b[A-Za-z0-9\-]+\.(com|net|org|io|me|gg|app|ly)(/\S*)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"(^|[\s(])@[A-Za-z0-9_.]{2,}")]
    private static partial Regex SocialHandle();

    [GeneratedRegex(@"\b(snap(chat)?|insta(gram)?|whats\s?app|telegram|discord|tik\s?tok|kik|facebook|messenger|wechat|line app|signal app|imessage|facetime|google chat|hangouts|skype|roblox chat)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MessagingApp();

    [GeneratedRegex(@"\b(text|call|dm|pm|message|ring|add|follow)\s+me\b|\bmy\s+(number|cell|phone|handle|username|user name)\b|\bdon'?t\s+tell\b|\bour\s+secret\b|\bdelete\s+(this|these)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateRequest();
}
