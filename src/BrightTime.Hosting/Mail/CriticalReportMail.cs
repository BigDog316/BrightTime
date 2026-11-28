using System.Net;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Mail;

/// <summary>
/// Emails a moderator that a critical report is waiting (D115), as Community booked it with the
/// report: its reason and a link to the moderation queue, to be taken on within the hour. Nothing
/// of the message or who wrote it: that is read in the console, where reading it is audited.
/// </summary>
/// <remarks>
/// Always sent, worded in the county's language. A recipient with no adult address, or a refused
/// send, is logged and not retried.
/// </remarks>
public sealed partial class CriticalReportMailHandler(
    IIdentityQueries identity,
    ISiteAddress site,
    Text text,
    IEmailSender email,
    ILogger<CriticalReportMailHandler> logger) : IJobHandler
{
    public static string JobType => CriticalReportNotice.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        CriticalReportNotice notice = context.ReadPayload<CriticalReportNotice>();
        IReadOnlyDictionary<Guid, string> addresses = await identity.GetAdultEmailsAsync([notice.RecipientUserId], cancellationToken).ConfigureAwait(false);
        if (!addresses.TryGetValue(notice.RecipientUserId, out string? to))
        {
            LogNoAddress(logger, context.Job.Id);
            return;
        }

        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        Uri link = new(await site.BaseUrlAsync(cancellationToken).ConfigureAwait(false), "organiser/moderation");
        DeliveryReceipt sent = await email.SendAsync(Compose(to, text, notice.Reason, link), cancellationToken).ConfigureAwait(false);
        if (!sent.Accepted)
        {
            LogRefused(logger, context.Job.Id, sent.ErrorCode ?? "unknown");
        }
    }

    /// <summary>The email, every value encoded, the direction set for right-to-left languages.</summary>
    public static EmailMessage Compose(string to, Text t, string reason, Uri link)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(link);
        string why = t.Has("moderation.report_reason." + reason) ? t["moderation.report_reason." + reason] : reason;
        string subject = t["email.critical_report.subject"];
        string intro = t.Format("email.critical_report.intro", ("reason", why));
        string button = t["email.critical_report.button"];
        string footer = t["email.critical_report.why"];
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             <p>{WebUtility.HtmlEncode(intro)}</p>
             <p><a href="{WebUtility.HtmlEncode(link.AbsoluteUri)}">{WebUtility.HtmlEncode(button)}</a></p>
             <p>{WebUtility.HtmlEncode(footer)}</p>
             </div>
             """;
        return new EmailMessage(to, subject, html, string.Join("\n\n", intro, link.AbsoluteUri, footer), locale);
    }

    /// <summary>
    /// A critical report from someone not signed in (D128): the moderators who may decide it, each
    /// emailed as a signed-in report's are. On the worker, which reads who they are.
    /// </summary>
    public static async Task FanOutAsync(
        CriticalPublicReport report, IIdentityQueries identity, IDirectoryQueries directory, Func<string, Task> send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(send);
        IReadOnlyList<OrganisationMembership> members = report.OrgId == Guid.Empty
            ? []
            : await directory.GetOrganisationMembersAsync(new OrganisationId(report.OrgId), cancellationToken).ConfigureAwait(false);
        List<Guid> moderators = [.. (await identity.GetUsersWithPermissionAsync("moderation.triage", cancellationToken).ConfigureAwait(false))
            .Where(m => !members.Any(o => o.UserId.Value == m))];
        foreach (string to in (await identity.GetAdultEmailsAsync(moderators, cancellationToken).ConfigureAwait(false)).Values)
        {
            await send(to).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 4331, Level = LogLevel.Information, Message = "Critical report email {Job}: the moderator has no adult address, so none was sent.")]
    private static partial void LogNoAddress(ILogger logger, Guid job);

    [LoggerMessage(EventId = 4332, Level = LogLevel.Warning, Message = "Critical report email {Job} was refused by the mail server: {Error}.")]
    private static partial void LogRefused(ILogger logger, Guid job, string error);
}

/// <summary>Emails every moderator who may decide a critical report from someone not signed in (D128).</summary>
public sealed partial class CriticalPublicReportHandler(
    IIdentityQueries identity,
    IDirectoryQueries directory,
    ISiteAddress site,
    Text text,
    IEmailSender email,
    ILogger<CriticalPublicReportHandler> logger) : IJobHandler
{
    public static string JobType => CriticalPublicReport.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        CriticalPublicReport report = context.ReadPayload<CriticalPublicReport>();
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        Uri link = new(await site.BaseUrlAsync(cancellationToken).ConfigureAwait(false), "organiser/moderation");
        await CriticalReportMailHandler.FanOutAsync(report, identity, directory, async to =>
        {
            DeliveryReceipt sent = await email.SendAsync(CriticalReportMailHandler.Compose(to, text, report.Reason, link), cancellationToken).ConfigureAwait(false);
            if (!sent.Accepted)
            {
                LogRefused(logger, context.Job.Id, sent.ErrorCode ?? "unknown");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 4333, Level = LogLevel.Warning, Message = "A critical public report's email (job {Job}) was refused by the mail server: {Error}.")]
    private static partial void LogRefused(ILogger logger, Guid job, string error);
}
