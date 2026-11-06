using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;

namespace BrightTime.Modules.Learning.Records;

/// <summary>
/// What a young person is told when staff check their work or they earn a badge (D83): in the
/// inbox, and as a push when they allow it. The words name the organisation and, for a playlist,
/// its title and the badge's name, all of them the organisation's own; never what the young person
/// wrote, which a locked phone would show.
/// </summary>
internal static class WorkNotices
{
    /// <summary>A project or playlist step approved or sent back (<c>ck_notification_kind</c>).</summary>
    public const string WorkReviewedKind = "work_reviewed";

    /// <summary>A badge awarded, by staff or for finishing a playlist (<c>ck_notification_kind</c>).</summary>
    public const string BadgeAwardedKind = "badge_awarded";

    public const string ProjectApproved = "notify.project.approved";
    public const string ProjectSentBack = "notify.project.sent_back";
    public const string StepApproved = "notify.step.approved";
    public const string StepSentBack = "notify.step.sent_back";
    public const string PlaylistCompleted = "notify.playlist.completed";
    public const string BadgeAwarded = "notify.badge.awarded";

    /// <summary>Queues the notice with the change, so it goes only if the change is kept.</summary>
    public static Task TellAsync(
        LearningDbContext db, ITenantContext tenant, Guid userId, string kind, string titleKey,
        IReadOnlyDictionary<string, string> arguments, string deepLink, string dedupeKey, CancellationToken ct) =>
        db.PublishAsync(tenant.TenantId, new NotificationRequested(userId, kind, titleKey, arguments, deepLink, dedupeKey), ct);

    /// <summary>The arguments, by name, as the catalogue's placeholders read them.</summary>
    public static IReadOnlyDictionary<string, string> Args(params (string Name, string Value)[] pairs)
    {
        Dictionary<string, string> args = new(StringComparer.Ordinal);
        foreach ((string name, string value) in pairs)
        {
            args[name] = value;
        }

        return args;
    }

    /// <summary>A key for each review: the same work may be sent back, sent again and checked again.</summary>
    public static string Key(string what, Guid id, DateTimeOffset at) =>
        what + ":" + id.ToString("N") + ":" + at.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
