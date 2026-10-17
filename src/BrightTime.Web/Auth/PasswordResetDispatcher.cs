using System.Net;
using System.Threading.Channels;
using BrightTime.Abstractions.Messaging;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Web.Auth;

/// <summary>
/// Handles "forgot password" after the response has gone. The request only queues the tenant
/// and the typed address; looking up the account, minting the token and sending the email all
/// happen here. So the request does exactly the same work whether or not the account exists,
/// and neither its response nor its timing says which (T21).
/// </summary>
/// <remarks>
/// In-process and not durable: a request queued just before a restart is lost, and the person
/// asks again. A full queue drops the request, which is logged without the address.
/// </remarks>
public sealed partial class PasswordResetDispatcher(
    IServiceScopeFactory scopes,
    ILogger<PasswordResetDispatcher> logger) : BackgroundService
{
    private readonly Channel<(TenantId Tenant, string Email, Uri SiteUrl, Locale PageLocale)> _requests =
        Channel.CreateBounded<(TenantId, string, Uri, Locale)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>
    /// Queues a reset. <paramref name="siteUrl"/> is the tenant's own address, from
    /// <see cref="Tenancy.SiteContext"/>; <paramref name="pageLocale"/> is the language the form
    /// was in, used when the account has no language of its own.
    /// </summary>
    public void Request(TenantId tenant, string email, Uri siteUrl, Locale pageLocale)
    {
        if (!_requests.Writer.TryWrite((tenant, email, siteUrl, pageLocale)))
        {
            LogDropped(logger);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach ((TenantId tenant, string email, Uri siteUrl, Locale pageLocale) in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                PasswordResetRequest? reset = await scope.ServiceProvider.GetRequiredService<PasswordAccounts>()
                    .CreateResetAsync(tenant, email, stoppingToken).ConfigureAwait(false);
                if (reset is null)
                {
                    continue;
                }

                // In the account's own language when it has one; otherwise the form's.
                Locale locale = Locale.TryFromCode(reset.Locale, out Locale own) ? own : pageLocale;
                var context = scope.ServiceProvider.GetRequiredService<Infrastructure.Tenancy.TenantContext>();
                context.SetTenant(tenant, TimeZoneInfo.Utc, locale);
                Localisation.Text text = scope.ServiceProvider.GetRequiredService<Localisation.Text>();
                await text.LoadAsync(stoppingToken).ConfigureAwait(false);

                DeliveryReceipt receipt = await scope.ServiceProvider.GetRequiredService<IEmailSender>()
                    .SendAsync(Compose(reset, siteUrl, text), stoppingToken).ConfigureAwait(false);

                // Recorded only when there was an account: a request for an unknown address
                // leaves no trace, so the audit log is no list of addresses people typed.

                // The actor stays anonymous: whoever asked was not signed in. The request's
                // address is gone by now, so this row has no client hashes.
                Infrastructure.Auditing.AuditTrail audit = scope.ServiceProvider.GetRequiredService<Infrastructure.Auditing.AuditTrail>();
                audit.Record(Auditing.AuthAudit.Allowed(Auditing.AuthAudit.ResetRequested, null,
                    detail: new Dictionary<string, string> { ["email_sent"] = receipt.Accepted ? "true" : "false" }) with
                {
                    SubjectKind = "user",
                    SubjectIds = [reset.UserId.Value],
                });
                await audit.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failed reset must not stop the dispatcher for everyone else.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
    }

    private static EmailMessage Compose(PasswordResetRequest reset, Uri baseUrl, Localisation.Text t)
    {
        string link = new Uri(baseUrl, $"/account/reset?token={Uri.EscapeDataString(reset.Token)}").AbsoluteUri;
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;

        string asked = t.Format("email.reset.asked", ("email", reset.Email));
        string useIt = t.Format("email.reset.use_link", ("minutes", minutes));
        string notYou = t["email.reset.not_you"];

        string text =
            $"""
             {asked}

             {useIt}

             {link}

             {notYou}
             """;

        // Every value encoded; the direction set for Arabic, since mail clients do not guess.
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             <p>{WebUtility.HtmlEncode(asked)}</p>
             <p>{WebUtility.HtmlEncode(useIt)}</p>
             <p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(t["email.reset.button"])}</a></p>
             <p>{WebUtility.HtmlEncode(notYou)}</p>
             </div>
             """;

        return new EmailMessage(reset.Email, t["email.reset.subject"], html, text, locale);
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "The password reset queue is full; a request was dropped.")]
    private static partial void LogDropped(ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A password reset failed: {Exception}.")]
    private static partial void LogFailed(ILogger logger, string exception);
}
