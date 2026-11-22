using System.Net;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Mail;

/// <summary>
/// Emails an organisation's owner or admin that a staff member's clearance expires soon or has
/// lapsed (D98), as Directory's sweep booked it. The staff member is named by their address, which
/// their organisation's admins already see on the staff page; nothing else about them is said.
/// </summary>
/// <remarks>
/// Worded in the county's language: an admin's own language isn't kept. A recipient with no adult
/// address, or a refused send, is logged and not retried; the inbox notice was sent either way.
/// </remarks>
public sealed partial class ClearanceMailHandler(
    IIdentityQueries identity, IDirectoryQueries directory, ISiteAddress site, Text text, IEmailSender email, ILogger<ClearanceMailHandler> logger) : IJobHandler
{
    public static string JobType => ClearanceNotice.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ClearanceNotice notice = context.ReadPayload<ClearanceNotice>();
        IReadOnlyDictionary<Guid, string> addresses = await identity.GetAdultEmailsAsync([notice.RecipientUserId, notice.StaffUserId], cancellationToken).ConfigureAwait(false);
        if (!addresses.TryGetValue(notice.RecipientUserId, out string? to))
        {
            LogNoAddress(logger, context.Job.Id);
            return;
        }

        string? org = await directory.GetOrganisationNameAsync(new OrganisationId(notice.OrgId), cancellationToken).ConfigureAwait(false);
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        Uri link = new(await site.BaseUrlAsync(cancellationToken).ConfigureAwait(false), $"provider/orgs/{notice.OrgId:D}/staff/{notice.MembershipId:D}/clearances");
        EmailMessage message = Compose(to, text, notice, org ?? string.Empty, addresses.GetValueOrDefault(notice.StaffUserId) ?? text["email.clearance.someone"], link);
        DeliveryReceipt sent = await email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!sent.Accepted)
        {
            LogRefused(logger, context.Job.Id, sent.ErrorCode ?? "unknown");
        }
    }

    /// <summary>The email, every value encoded, the direction set for right-to-left languages.</summary>
    public static EmailMessage Compose(string to, Text t, ClearanceNotice notice, string organisation, string person, Uri link)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(link);
        string kind = t["clearance.kind." + notice.Kind];
        (string, object)[] args =
        [
            ("kind", kind), ("person", Text.Name(person)), ("organisation", Text.Name(organisation)),
            ("date", t.DateShort(notice.ExpiresOn)),
        ];
        string what = notice.Notice == ClearanceNotice.Lapsed ? "lapsed" : "expiring";
        string subject = t.Format("email.clearance." + what + ".subject", args);
        string[] paragraphs = [t.Format("email.clearance." + what + ".intro", args), t[$"email.clearance.{what}.next"]];
        string button = t["email.clearance.button"];
        string footer = t.Format("email.clearance.why", args);

        string plain = string.Join("\n\n", [.. paragraphs, link.AbsoluteUri, footer]);
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             {string.Concat(paragraphs.Select(p => $"<p>{WebUtility.HtmlEncode(p)}</p>\n"))}<p><a href="{WebUtility.HtmlEncode(link.AbsoluteUri)}">{WebUtility.HtmlEncode(button)}</a></p>
             <p>{WebUtility.HtmlEncode(footer)}</p>
             </div>
             """;
        return new EmailMessage(to, subject, html, plain, locale);
    }

    [LoggerMessage(EventId = 4311, Level = LogLevel.Information, Message = "Clearance email {Job}: the recipient has no adult address, so none was sent.")]
    private static partial void LogNoAddress(ILogger logger, Guid job);

    [LoggerMessage(EventId = 4312, Level = LogLevel.Warning, Message = "Clearance email {Job} was refused by the mail server: {Error}.")]
    private static partial void LogRefused(ILogger logger, Guid job, string error);
}
