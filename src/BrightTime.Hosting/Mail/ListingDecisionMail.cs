using System.Net;
using BrightTime.Abstractions.Messaging;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Mail;

/// <summary>
/// Emails someone at an organisation that a network organiser has decided on one of its listings
/// (D102), as Opportunities booked it with the decision: what was decided, the organiser's note
/// while it still stands, and a link to the listing in the console.
/// </summary>
/// <remarks>
/// Always sent; there's no setting, as for the clearance emails (D98). Worded in the county's
/// language: a staff member's own language isn't kept. A recipient with no adult address, a
/// listing that's gone, or a refused send is logged and not retried.
/// </remarks>
public sealed partial class ListingDecisionMailHandler(
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IListingDecisions listings,
    ISiteAddress site,
    Text text,
    IEmailSender email,
    ILogger<ListingDecisionMailHandler> logger) : IJobHandler
{
    public static string JobType => ListingDecisionNotice.JobType;

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ListingDecisionNotice notice = context.ReadPayload<ListingDecisionNotice>();
        if (!ListingDecisionNotice.Decisions.Contains(notice.Decision))
        {
            LogUnknownDecision(logger, context.Job.Id, notice.Decision);
            return;
        }

        IReadOnlyDictionary<Guid, string> addresses = await identity.GetAdultEmailsAsync([notice.RecipientUserId], cancellationToken).ConfigureAwait(false);
        if (!addresses.TryGetValue(notice.RecipientUserId, out string? to))
        {
            LogNoAddress(logger, context.Job.Id);
            return;
        }

        if (await listings.GetAsync(notice.ListingId, notice.Decision, cancellationToken).ConfigureAwait(false) is not { } facts)
        {
            LogGone(logger, context.Job.Id);
            return;
        }

        string? org = await directory.GetOrganisationNameAsync(new OrganisationId(notice.OrgId), cancellationToken).ConfigureAwait(false);
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        Uri link = new(await site.BaseUrlAsync(cancellationToken).ConfigureAwait(false), $"provider/orgs/{notice.OrgId:D}/listings/{notice.ListingId:D}");
        EmailMessage message = Compose(to, text, notice.Decision, facts, org ?? string.Empty, link);
        DeliveryReceipt sent = await email.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!sent.Accepted)
        {
            LogRefused(logger, context.Job.Id, sent.ErrorCode ?? "unknown");
        }
    }

    /// <summary>
    /// The email, every value encoded, the direction set for right-to-left languages. The note keeps
    /// its own direction and line breaks: it's in whatever language the organiser wrote it.
    /// </summary>
    public static EmailMessage Compose(string to, Text t, string decision, ListingDecisionFacts facts, string organisation, Uri link)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(link);
        (string, object)[] args = [("title", Text.Name(facts.Title)), ("organisation", Text.Name(organisation))];
        string subject = t.Format("email.listing_decision." + decision + ".subject", args);
        string intro = t.Format("email.listing_decision." + decision + ".intro", args);
        string? noteLabel = facts.Note is null ? null : t["email.listing_decision.note"];
        string button = t["email.listing_decision.button"];
        string footer = t.Format("email.listing_decision.why", args);

        List<string> plain = [intro];
        if (facts.Note is { } note)
        {
            plain.Add(noteLabel + "\n" + note);
        }

        plain.Add(link.AbsoluteUri);
        plain.Add(footer);

        string quoted = facts.Note is { } said
            ? $"<p>{WebUtility.HtmlEncode(noteLabel)}</p>\n<blockquote dir=\"auto\">{WebUtility.HtmlEncode(said).ReplaceLineEndings("<br>")}</blockquote>\n"
            : string.Empty;
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             <p>{WebUtility.HtmlEncode(intro)}</p>
             {quoted}<p><a href="{WebUtility.HtmlEncode(link.AbsoluteUri)}">{WebUtility.HtmlEncode(button)}</a></p>
             <p>{WebUtility.HtmlEncode(footer)}</p>
             </div>
             """;
        return new EmailMessage(to, subject, html, string.Join("\n\n", plain), locale);
    }

    [LoggerMessage(EventId = 4321, Level = LogLevel.Information, Message = "Listing decision email {Job}: the recipient has no adult address, so none was sent.")]
    private static partial void LogNoAddress(ILogger logger, Guid job);

    [LoggerMessage(EventId = 4322, Level = LogLevel.Information, Message = "Listing decision email {Job}: the listing is gone, so none was sent.")]
    private static partial void LogGone(ILogger logger, Guid job);

    [LoggerMessage(EventId = 4323, Level = LogLevel.Warning, Message = "Listing decision email {Job} names an unknown decision {Decision}; none was sent.")]
    private static partial void LogUnknownDecision(ILogger logger, Guid job, string decision);

    [LoggerMessage(EventId = 4324, Level = LogLevel.Warning, Message = "Listing decision email {Job} was refused by the mail server: {Error}.")]
    private static partial void LogRefused(ILogger logger, Guid job, string error);
}
