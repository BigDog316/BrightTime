using System.Net;
using System.Threading.Channels;
using BrightTime.Abstractions.Messaging;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Mail;

/// <summary>The account emails, sent after the response so that neither it nor its timing says whether an account exists.</summary>
public enum AccountMailKind
{
    /// <summary>"Forgot password": a reset link, if the address has an account.</summary>
    Reset,

    /// <summary>An organisation's application was received (D23).</summary>
    Welcome,

    /// <summary>Someone was invited to an organisation's staff (D23).</summary>
    Invitation,

    /// <summary>Someone registered from the app, for this address: an adult, or a young person of 13 to 17 (D35).</summary>
    Registration,

    /// <summary>A guardian is asked to consent to their child's account (email plus, D31).</summary>
    ConsentRequest,

    /// <summary>The confirmatory email after a guardian consented by email: the "plus".</summary>
    ConsentConfirmation,

    /// <summary>A young person, now 13, is sent the link to claim their child profile as their own sign-in (D35).</summary>
    Claim,
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
/// was found, since only the address's owner reads it. A registration is the same, made here
/// rather than in the request: a new account gets a set-password link, an existing one a
/// reset link and a word that it already exists.
/// </para>
/// <para>
/// Consent emails go to a guardian who is signed in, so they hide nothing; they come through
/// here so the request does not wait on the mail server. Their token was minted, and its hash
/// stored, by <see cref="Guardianship"/>. Each one sent is audited, with whether the server took it.
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
    private sealed record Request(
        AccountMailKind Kind,
        TenantId Tenant,
        string Email,
        Uri SiteUrl,
        Locale PageLocale,
        string? Organisation = null,
        bool NewAccount = false,
        ConsentEmail? Consent = null,
        ConsentConfirmation? Confirmation = null,
        (DateOnly Month, AgeBand Band)? Youth = null,
        ClaimAsk? Claim = null);

    private readonly record struct Paragraph(string Text, Locale? Language = null);

    private readonly Channel<Request> _requests =
        Channel.CreateBounded<Request>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    /// <summary>
    /// Queues a reset. <paramref name="siteUrl"/> is the tenant's own address, from
    /// <see cref="SiteContext"/>; <paramref name="pageLocale"/> is the language the form
    /// was in, used when the account has no language of its own.
    /// </summary>
    public void RequestReset(TenantId tenant, string email, Uri siteUrl, Locale pageLocale) =>
        Enqueue(new Request(AccountMailKind.Reset, tenant, email, siteUrl, pageLocale));

    /// <summary>
    /// Queues a welcome or an invitation for <paramref name="organisation"/>. When
    /// <paramref name="newAccount"/> holds, the account was made for this, and the mail carries
    /// a set-password link rather than a sign-in link.
    /// </summary>
    public void RequestMembership(AccountMailKind kind, TenantId tenant, string email, string organisation, bool newAccount, Uri siteUrl, Locale pageLocale)
    {
        if (kind is not (AccountMailKind.Welcome or AccountMailKind.Invitation))
        {
            throw new ArgumentException("Only a welcome or an invitation is a membership email.", nameof(kind));
        }

        Enqueue(new Request(kind, tenant, email, siteUrl, pageLocale, organisation, newAccount));
    }

    /// <summary>
    /// Queues a registration: the account for <paramref name="email"/> is found or made here
    /// (D23's <see cref="AccountProvisioning.FindOrCreateAdultAsync"/>), then sent a link to set a
    /// password. With a birth month of 13 to 17 it is made a youth account (D35), and its
    /// capabilities are booked for the worker. An existing account is left as it is; an address
    /// that belongs to a child profile gets nothing.
    /// </summary>
    public void RequestRegistration(TenantId tenant, string email, Uri siteUrl, Locale pageLocale, (DateOnly Month, AgeBand Band)? youth = null)
    {
        if (youth is { } y && (y.Month.Day != 1 || !y.Band.IsYouth()))
        {
            throw new ArgumentOutOfRangeException(nameof(youth), "A youth registration is for 13 to 17, by the first of the birth month.");
        }

        Enqueue(new Request(AccountMailKind.Registration, tenant, email, siteUrl, pageLocale, Youth: youth));
    }

    /// <summary>
    /// Queues a claim (D35): the link goes to the young person's address. When that address already
    /// has an account, its owner is told instead and nothing changes; the guardian's answer is the
    /// same either way.
    /// </summary>
    public void RequestClaim(TenantId tenant, ClaimAsk claim, Uri siteUrl, Locale pageLocale)
    {
        ArgumentNullException.ThrowIfNull(claim);
        Enqueue(new Request(AccountMailKind.Claim, tenant, claim.Email, siteUrl, pageLocale, Claim: claim));
    }

    /// <summary>The link a claim email carries: the web's page for choosing a password, which the app may also answer.</summary>
    public static Uri ClaimLink(Uri siteUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(siteUrl);
        return new Uri(siteUrl, $"/account/claim?token={Uri.EscapeDataString(token)}");
    }

    /// <summary>Queues a guardian's consent email, in their own language when they have one.</summary>
    public void RequestConsent(TenantId tenant, ConsentEmail consent, Uri siteUrl, Locale pageLocale)
    {
        ArgumentNullException.ThrowIfNull(consent);
        Enqueue(new Request(AccountMailKind.ConsentRequest, tenant, consent.GuardianEmail, siteUrl, pageLocale, Consent: consent));
    }

    /// <summary>Queues the confirmatory email to a guardian who has just consented by email.</summary>
    public void RequestConsentConfirmation(TenantId tenant, ConsentConfirmation confirmation, Uri siteUrl, Locale pageLocale)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        Enqueue(new Request(AccountMailKind.ConsentConfirmation, tenant, confirmation.GuardianEmail, siteUrl, pageLocale, Confirmation: confirmation));
    }

    /// <summary>
    /// The link a consent email carries: an app link on the tenant's own address, which the app
    /// claims and answers with <c>POST /api/v1/guardian-links/{id}/verify</c>. The token rides in
    /// the fragment, which no server or proxy log sees.
    /// </summary>
    public static Uri ConsentLink(Uri siteUrl, Guid linkId, string token)
    {
        ArgumentNullException.ThrowIfNull(siteUrl);
        return new Uri(siteUrl, $"/app/guardian-links/{linkId:D}/verify#token={Uri.EscapeDataString(token)}");
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
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                Task sending = request.Kind switch
                {
                    AccountMailKind.Registration => SendRegistrationAsync(scope.ServiceProvider, request, stoppingToken),
                    AccountMailKind.Claim => SendClaimAsync(scope.ServiceProvider, request, stoppingToken),
                    AccountMailKind.ConsentRequest or AccountMailKind.ConsentConfirmation => SendConsentAsync(scope.ServiceProvider, request, stoppingToken),
                    _ => SendAccountAsync(scope.ServiceProvider, request, stoppingToken),
                };
                await sending.ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failed email must not stop the dispatcher for everyone else.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, request.Kind.ToString(), ex.GetType().Name);
            }
        }
    }

    private static async Task SendAccountAsync(IServiceProvider services, Request request, CancellationToken ct)
    {
        // A set-password link is a reset token. An existing account gets none: it already has a way in.
        PasswordResetRequest? reset = null;
        if (request.Kind == AccountMailKind.Reset || request.NewAccount)
        {
            reset = await services.GetRequiredService<PasswordAccounts>()
                .CreateResetAsync(request.Tenant, request.Email, ct).ConfigureAwait(false);
            if (reset is null)
            {
                return;
            }
        }

        // In the account's own language when it has one; otherwise the form's.
        Text text = await TextAsync(services, request, reset?.Locale, ct).ConfigureAwait(false);
        EmailMessage message = request.Kind == AccountMailKind.Reset
            ? ComposeReset(reset!, request.SiteUrl, text)
            : ComposeMembership(request, reset, text);
        DeliveryReceipt receipt = await services.GetRequiredService<IEmailSender>()
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
        await AuditAsync(services, AuthAudit.Allowed(AuthAudit.ResetRequested, null, detail: Sent(receipt)) with
        {
            SubjectKind = "user",
            SubjectIds = [reset!.UserId.Value],
        }, ct).ConfigureAwait(false);
    }

    private static async Task SendRegistrationAsync(IServiceProvider services, Request request, CancellationToken ct)
    {
        AccountProvisioning provisioning = services.GetRequiredService<AccountProvisioning>();
        (UserId User, bool Created)? account = request.Youth is { } youth
            ? await provisioning.FindOrCreateYouthAsync(request.Tenant, request.Email, youth.Month, youth.Band, ct).ConfigureAwait(false)
            : await provisioning.FindOrCreateAdultAsync(request.Tenant, request.Email, ct).ConfigureAwait(false);
        if (account is null)
        {
            // A child profile's address: those are a guardian's, never from here.
            return;
        }

        if (account.Value.Created && request.Youth is not null)
        {
            // Nobody is signed in to recompute as; the worker writes the new account's capabilities.
            services.GetRequiredService<TenantContext>().SetTenant(request.Tenant, TimeZoneInfo.Utc, request.PageLocale);
            await services.GetRequiredService<Guardianship>().RequestRecomputeAsync(account.Value.User, "registered", ct).ConfigureAwait(false);
        }

        // New or not, the way in is the same link: set a password, or set a new one.
        PasswordResetRequest? reset = await services.GetRequiredService<PasswordAccounts>()
            .CreateResetAsync(request.Tenant, request.Email, ct).ConfigureAwait(false);
        if (reset is null)
        {
            // Asked again within the resend interval: the first email is still on its way.
            return;
        }

        Text t = await TextAsync(services, request, reset.Locale, ct).ConfigureAwait(false);
        bool created = account.Value.Created;
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;
        EmailMessage message = Compose(reset.Email, t["email.register.subject"], t,
            [
                new(created ? t.Format("email.register.intro_new", ("email", reset.Email)) : t.Format("email.register.intro_existing", ("email", reset.Email))),
                new(created ? t.Format("email.membership.set_password", ("minutes", minutes)) : t.Format("email.reset.use_link", ("minutes", minutes))),
            ],
            ResetLink(request.SiteUrl, reset),
            created ? t["email.membership.set_password_button"] : t["email.reset.button"],
            t["email.register.not_you"]);
        DeliveryReceipt receipt = await services.GetRequiredService<IEmailSender>().SendAsync(message, ct).ConfigureAwait(false);

        Dictionary<string, string> detail = Sent(receipt);
        detail["created"] = created ? "true" : "false";
        await AuditAsync(services, AuthAudit.Allowed(AuthAudit.RegistrationRequested, null, detail: detail) with
        {
            SubjectKind = "user",
            SubjectIds = [account.Value.User.Value],
        }, ct).ConfigureAwait(false);
    }

    private static async Task SendClaimAsync(IServiceProvider services, Request request, CancellationToken ct)
    {
        ClaimAsk claim = request.Claim!;
        AccountClaimIssue issue = await services.GetRequiredService<PasswordAccounts>()
            .CreateClaimAsync(request.Tenant, claim.ChildId, claim.Email, ct).ConfigureAwait(false);

        if (issue.AddressInUse)
        {
            // Only the address's owner reads this, so it may say the address is taken. The guardian's
            // answer said nothing, and nothing changed.
            Text text = await TextAsync(services, request, issue.InUseLocale, ct).ConfigureAwait(false);
            await services.GetRequiredService<IEmailSender>().SendAsync(
                Compose(request.Email, text["email.claim_in_use.subject"], text,
                    [new(text["email.claim_in_use.intro"]), new(text["email.claim_in_use.next"])],
                    request.SiteUrl.AbsoluteUri, text["email.claim_in_use.button"], text["email.claim_in_use.not_you"]),
                ct).ConfigureAwait(false);
            return;
        }

        if (issue.Claim is not { } link)
        {
            // Asked again within the resend interval, or the profile changed meanwhile.
            return;
        }

        Text t = await TextAsync(services, request, link.Locale, ct).ConfigureAwait(false);
        int days = (int)PasswordAccounts.ClaimLifetime.TotalDays;
        DeliveryReceipt receipt = await services.GetRequiredService<IEmailSender>().SendAsync(
            Compose(link.Email, t["email.claim.subject"], t,
                [new(t["email.claim.intro"]), new(t.Format("email.claim.use_link", ("days", days)))],
                ClaimLink(request.SiteUrl, link.Token).AbsoluteUri, t["email.claim.button"], t["email.claim.not_you"]),
            ct).ConfigureAwait(false);

        await AuditAsync(services, new AuditAccess
        {
            Action = Guardianship.ClaimEmailSent,
            SubjectKind = "user",
            SubjectIds = [claim.ChildId.Value],
            Detail = Sent(receipt),
        }, ct).ConfigureAwait(false);
    }

    private static async Task SendConsentAsync(IServiceProvider services, Request request, CancellationToken ct)
    {
        bool asking = request.Kind == AccountMailKind.ConsentRequest;
        Text t = await TextAsync(services, request, asking ? request.Consent!.GuardianLocale : request.Confirmation!.GuardianLocale, ct).ConfigureAwait(false);
        EmailMessage message = asking ? ComposeConsentRequest(request, t) : ComposeConsentConfirmation(request, t);
        DeliveryReceipt receipt = await services.GetRequiredService<IEmailSender>().SendAsync(message, ct).ConfigureAwait(false);

        // What the guardian was sent, and when, is part of the consent's record.
        await AuditAsync(services, new AuditAccess
        {
            Action = asking ? Guardianship.ConsentEmailSent : Guardianship.ConfirmationSent,
            SubjectKind = "user",
            SubjectIds = [(asking ? request.Consent!.ChildId : request.Confirmation!.ChildId).Value],
            Detail = Sent(receipt),
        }, ct).ConfigureAwait(false);
    }

    private static EmailMessage ComposeReset(PasswordResetRequest reset, Uri baseUrl, Text t)
    {
        string link = ResetLink(baseUrl, reset);
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;

        return Compose(reset.Email, t["email.reset.subject"], t,
            [new(t.Format("email.reset.asked", ("email", reset.Email))), new(t.Format("email.reset.use_link", ("minutes", minutes)))],
            link, t["email.reset.button"], t["email.reset.not_you"]);
    }

    private static EmailMessage ComposeMembership(Request request, PasswordResetRequest? reset, Text t)
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

        return Compose(request.Email, subject, t, [new(intro), new(next)], link, button, footer);
    }

    /// <summary>
    /// The notice and the request (16 CFR 312.4(c)(1)): what the account holds, in the exact
    /// words the consent will be recorded against, and how to say yes.
    /// </summary>
    private static EmailMessage ComposeConsentRequest(Request request, Text t)
    {
        ConsentEmail consent = request.Consent!;
        (string, object) child = ("child", Text.Name(consent.ChildGivenName));
        int days = (int)Guardianship.EmailTokenLifetime.TotalDays;

        return Compose(consent.GuardianEmail, t.Format("email.consent.request.subject", child), t,
            [
                new(t.Format("email.consent.request.intro", child)),
                .. Disclosure(t),
                new(t.Format("email.consent.request.use_link", ("days", days))),
            ],
            ConsentLink(request.SiteUrl, consent.LinkId, consent.Token).AbsoluteUri,
            t["email.consent.request.button"],
            t["email.consent.request.not_you"]);
    }

    /// <summary>The "plus": consent was given, to these words, and can be withdrawn at any time.</summary>
    private static EmailMessage ComposeConsentConfirmation(Request request, Text t)
    {
        ConsentConfirmation confirmed = request.Confirmation!;
        (string, object) child = ("child", Text.Name(confirmed.ChildGivenName));

        return Compose(confirmed.GuardianEmail, t.Format("email.consent.confirmed.subject", child), t,
            [
                new(t.Format("email.consent.confirmed.intro", child)),
                .. Disclosure(t),
                new(t["email.consent.confirmed.withdraw"]),
            ],
            request.SiteUrl.AbsoluteUri,
            t["email.consent.confirmed.button"],
            t["email.consent.confirmed.not_you"]);
    }

    /// <summary>
    /// The account disclosure, verbatim: its hash is what the consent row records. Only English
    /// exists until the texts are reviewed and translated (Q13), so other languages say so.
    /// </summary>
    private static Paragraph[] Disclosure(Text t)
    {
        ConsentDisclosure disclosure = ConsentDisclosures.For(ConsentScope.Account);
        Paragraph text = new(disclosure.Text, Locale.English);
        return t.Locale == Locale.English
            ? [new(t["email.consent.disclosure_heading"]), text]
            : [new(t["email.consent.disclosure_heading"]), new(t["email.consent.disclosure_in_english"]), text];
    }

    private static string ResetLink(Uri baseUrl, PasswordResetRequest reset) =>
        new Uri(baseUrl, $"/account/reset?token={Uri.EscapeDataString(reset.Token)}").AbsoluteUri;

    private static EmailMessage Compose(string to, string subject, Text t, Paragraph[] paragraphs, string link, string button, string footer)
    {
        string text = string.Join("\n\n", [.. paragraphs.Select(p => p.Text), link, footer]);

        // Every value encoded; the direction set for Arabic, since mail clients do not guess. A
        // paragraph in another language than the email's says which.
        Locale locale = t.Locale;
        string html =
            $"""
             <div lang="{locale.Code}" dir="{locale.Direction}">
             {string.Concat(paragraphs.Select(Html))}<p><a href="{WebUtility.HtmlEncode(link)}">{WebUtility.HtmlEncode(button)}</a></p>
             <p>{WebUtility.HtmlEncode(footer)}</p>
             </div>
             """;

        return new EmailMessage(to, subject, html, text, locale);

        string Html(Paragraph p) => p.Language is { } language && language != locale
            ? $"<p lang=\"{language.Code}\" dir=\"{language.Direction}\">{WebUtility.HtmlEncode(p.Text)}</p>\n"
            : $"<p>{WebUtility.HtmlEncode(p.Text)}</p>\n";
    }

    /// <summary>Text for the request's tenant, in <paramref name="ownLocale"/> when it is one, else the page's.</summary>
    private static async Task<Text> TextAsync(IServiceProvider services, Request request, string? ownLocale, CancellationToken ct)
    {
        Locale locale = Locale.TryFromCode(ownLocale, out Locale own) ? own : request.PageLocale;
        services.GetRequiredService<TenantContext>().SetTenant(request.Tenant, TimeZoneInfo.Utc, locale);
        Text text = services.GetRequiredService<Text>();
        await text.LoadAsync(ct).ConfigureAwait(false);
        return text;
    }

    private static Dictionary<string, string> Sent(DeliveryReceipt receipt) =>
        new(StringComparer.Ordinal) { ["email_sent"] = receipt.Accepted ? "true" : "false" };

    private static async Task AuditAsync(IServiceProvider services, AuditAccess access, CancellationToken ct)
    {
        AuditTrail audit = services.GetRequiredService<AuditTrail>();
        audit.Record(access);
        await audit.FlushAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "The account email queue is full; a {Kind} email was dropped.")]
    private static partial void LogDropped(ILogger logger, string kind);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A {Kind} email failed: {Exception}.")]
    private static partial void LogFailed(ILogger logger, string kind, string exception);
}
