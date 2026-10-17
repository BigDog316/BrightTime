using System.Net;
using System.Threading.Channels;
using BrightTime.Abstractions.Messaging;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Web.Auth;

/// <summary>The account emails that must not reveal, by their response or their timing, whether an account exists.</summary>
public enum AccountMailKind
{
    /// <summary>"Forgot password": a reset link, if the address has an account.</summary>
    Reset,

    /// <summary>An organisation's application was received (D23).</summary>
    Welcome,

    /// <summary>Someone was invited to an organisation's staff (D23).</summary>
    Invitation,
}

/// <summary>
/// Sends account emails after the response has gone. The request only queues the tenant, the
/// address and what happened; looking up the account, minting any token and sending all happen
/// here. So a request does the same work whether or not the account exists, and neither its
/// response nor its timing says which (T21).
/// </summary>
/// <remarks>
/// <para>
/// A welcome or an invitation to an account made just now carries a set-password link (the
/// reset link, which also confirms the address). One to an account that already existed
/// carries a sign-in link, and says what to accept once signed in; it never says the account
/// was found, since only the address's owner reads it.
/// </para>
/// <para>
/// In-process and not durable: a request queued just before a restart is lost, and the person
/// asks again. A full queue drops the request, which is logged without the address.
/// </para>
/// </remarks>
public sealed partial class AccountMailDispatcher(
    IServiceScopeFactory scopes,
    ILogger<AccountMailDispatcher> logger) : BackgroundService
{
    private sealed record Request(AccountMailKind Kind, TenantId Tenant, string Email, Uri SiteUrl, Locale PageLocale, string? Organisation, bool NewAccount);

    private readonly Channel<Request> _requests =
        Channel.CreateBounded<Request>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>
    /// Queues a reset. <paramref name="siteUrl"/> is the tenant's own address, from
    /// <see cref="Tenancy.SiteContext"/>; <paramref name="pageLocale"/> is the language the form
    /// was in, used when the account has no language of its own.
    /// </summary>
    public void RequestReset(TenantId tenant, string email, Uri siteUrl, Locale pageLocale) =>
        Enqueue(new Request(AccountMailKind.Reset, tenant, email, siteUrl, pageLocale, null, NewAccount: false));

    /// <summary>
    /// Queues a welcome or an invitation for <paramref name="organisation"/>. When
    /// <paramref name="newAccount"/> holds, the account was made for this, and the mail carries
    /// a set-password link rather than a sign-in link.
    /// </summary>
    public void RequestMembership(AccountMailKind kind, TenantId tenant, string email, string organisation, bool newAccount, Uri siteUrl, Locale pageLocale)
    {
        if (kind == AccountMailKind.Reset)
        {
            throw new ArgumentException("A reset is requested with RequestReset.", nameof(kind));
        }

        Enqueue(new Request(kind, tenant, email, siteUrl, pageLocale, organisation, newAccount));
    }

    private void Enqueue(Request request)
    {
        if (!_requests.Writer.TryWrite(request))
        {
            LogDropped(logger, request.Kind.ToString());
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (Request request in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SendAsync(request, stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failed email must not stop the dispatcher for everyone else.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, request.Kind.ToString(), ex.GetType().Name);
            }
        }
    }

    private async Task SendAsync(Request request, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        // A set-password link is a reset token. An existing account gets none: it already has a way in.
        PasswordResetRequest? reset = null;
        if (request.Kind == AccountMailKind.Reset || request.NewAccount)
        {
            reset = await scope.ServiceProvider.GetRequiredService<PasswordAccounts>()
                .CreateResetAsync(request.Tenant, request.Email, ct).ConfigureAwait(false);
            if (reset is null)
            {
                return;
            }
        }

        // In the account's own language when it has one; otherwise the form's.
        Locale locale = reset is not null && Locale.TryFromCode(reset.Locale, out Locale own) ? own : request.PageLocale;
        var context = scope.ServiceProvider.GetRequiredService<Infrastructure.Tenancy.TenantContext>();
        context.SetTenant(request.Tenant, TimeZoneInfo.Utc, locale);
        Localisation.Text text = scope.ServiceProvider.GetRequiredService<Localisation.Text>();
        await text.LoadAsync(ct).ConfigureAwait(false);

        EmailMessage message = request.Kind == AccountMailKind.Reset
            ? ComposeReset(reset!, request.SiteUrl, text)
            : ComposeMembership(request, reset, text);
        DeliveryReceipt receipt = await scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendAsync(message, ct).ConfigureAwait(false);

        if (request.Kind != AccountMailKind.Reset)
        {
            // The application or invitation that caused this mail is already on record.
            return;
        }

        // Recorded only when there was an account: a request for an unknown address
        // leaves no trace, so the audit log is no list of addresses people typed.

        // The actor stays anonymous: whoever asked was not signed in. The request's
        // address is gone by now, so this row has no client hashes.
        Infrastructure.Auditing.AuditTrail audit = scope.ServiceProvider.GetRequiredService<Infrastructure.Auditing.AuditTrail>();
        audit.Record(Auditing.AuthAudit.Allowed(Auditing.AuthAudit.ResetRequested, null,
            detail: new Dictionary<string, string> { ["email_sent"] = receipt.Accepted ? "true" : "false" }) with
        {
            SubjectKind = "user",
            SubjectIds = [reset!.UserId.Value],
        });
        await audit.FlushAsync(ct).ConfigureAwait(false);
    }

    private static EmailMessage ComposeReset(PasswordResetRequest reset, Uri baseUrl, Localisation.Text t)
    {
        string link = ResetLink(baseUrl, reset);
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;

        return Compose(reset.Email, t["email.reset.subject"], t,
            [t.Format("email.reset.asked", ("email", reset.Email)), t.Format("email.reset.use_link", ("minutes", minutes))],
            link, t["email.reset.button"], t["email.reset.not_you"]);
    }

    private static EmailMessage ComposeMembership(Request request, PasswordResetRequest? reset, Localisation.Text t)
    {
        bool welcome = request.Kind == AccountMailKind.Welcome;
        (string, object)[] organisation = [("organisation", request.Organisation!)];
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;

        string subject = welcome ? t.Format("email.welcome.subject", organisation) : t.Format("email.invitation.subject", organisation);
        string intro = welcome ? t.Format("email.welcome.intro", organisation) : t.Format("email.invitation.intro", organisation);
        string footer = welcome ? t["email.welcome.not_you"] : t["email.invitation.not_you"];
        string next = reset is not null ? t.Format("email.membership.set_password", ("minutes", minutes)) : t["email.membership.sign_in"];
        string button = reset is not null ? t["email.membership.set_password_button"] : t["email.membership.sign_in_button"];
        string link = reset is not null ? ResetLink(request.SiteUrl, reset) : new Uri(request.SiteUrl, "/sign-in?returnUrl=%2Fprovider").AbsoluteUri;

        return Compose(request.Email, subject, t, [intro, next], link, button, footer);
    }

    private static string ResetLink(Uri baseUrl, PasswordResetRequest reset) =>
        new Uri(baseUrl, $"/account/reset?token={Uri.EscapeDataString(reset.Token)}").AbsoluteUri;

    private static EmailMessage Compose(string to, string subject, Localisation.Text t, string[] paragraphs, string link, string button, string footer)
    {
        string text = string.Join("\n\n", [.. paragraphs, link, footer]);

        // Every value encoded; the direction set for Arabic, since mail clients do not guess.
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             {string.Concat(paragraphs.Select(p => $"<p>{WebUtility.HtmlEncode(p)}</p>\n"))}<p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(button)}</a></p>
             <p>{WebUtility.HtmlEncode(footer)}</p>
             </div>
             """;

        return new EmailMessage(to, subject, html, text, locale);
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "The account email queue is full; a {Kind} email was dropped.")]
    private static partial void LogDropped(ILogger logger, string kind);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A {Kind} email failed: {Exception}.")]
    private static partial void LogFailed(ILogger logger, string kind, string exception);
}
