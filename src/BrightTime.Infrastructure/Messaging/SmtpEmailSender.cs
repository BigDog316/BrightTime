using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using BrightTime.Abstractions.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BrightTime.Infrastructure.Messaging;

/// <summary>
/// Sends through SMTP, or writes to a pickup directory. Plain text and HTML alternatives, so
/// every client can read the message.
/// </summary>
/// <remarks>
/// Q9 (the delivery provider) is still open; SMTP works with mailpit, with every relay and
/// with most providers, so nothing here waits on that decision.
/// </remarks>
public sealed partial class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task<DeliveryReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EmailOptions o = options.Value;

        if (!o.IsConfigured)
        {
            LogNotConfigured(logger, message.Subject);
            return new DeliveryReceipt(false, null, "not_configured");
        }

        using var mail = new MailMessage { From = new MailAddress(o.From), Subject = message.Subject, SubjectEncoding = System.Text.Encoding.UTF8 };
        mail.To.Add(message.To);
        if (message.ReplyTo is not null)
        {
            mail.ReplyToList.Add(message.ReplyTo);
        }

        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, System.Text.Encoding.UTF8, MediaTypeNames.Text.Plain));
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.HtmlBody, System.Text.Encoding.UTF8, MediaTypeNames.Text.Html));
        mail.Headers.Add("Content-Language", message.Locale.Code);

        using SmtpClient client = Client(o);
        try
        {
            await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
            return new DeliveryReceipt(true, mail.Headers["Message-ID"]);
        }
        catch (SmtpException ex)
        {
            LogFailed(logger, message.Subject, ex.StatusCode.ToString());
            return new DeliveryReceipt(false, null, ex.StatusCode.ToString());
        }
    }

    private static SmtpClient Client(EmailOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.PickupDirectory) && string.IsNullOrWhiteSpace(o.Host))
        {
            Directory.CreateDirectory(o.PickupDirectory);
            return new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = Path.GetFullPath(o.PickupDirectory) };
        }

        var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.EnableSsl };
        if (!string.IsNullOrEmpty(o.UserName))
        {
            client.Credentials = new NetworkCredential(o.UserName, o.Password);
        }

        return client;
    }

    // The recipient is never logged: an address is personal data, and a reset email's
    // recipient is exactly what an attacker probing for accounts wants to learn.
    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Email is not configured; \"{Subject}\" was not sent. Set Email:Host or Email:PickupDirectory.")]
    private static partial void LogNotConfigured(ILogger logger, string subject);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Sending \"{Subject}\" failed: {Status}.")]
    private static partial void LogFailed(ILogger logger, string subject, string status);
}
