using System.Net;
using System.Threading.Channels;
using BrightTime.Abstractions.Messaging;
using BrightTime.Modules.Identity.SignIn;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.Extensions.Options;

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
    IOptions<AuthOptions> options,
    ILogger<PasswordResetDispatcher> logger) : BackgroundService
{
    private readonly Channel<(TenantId Tenant, string Email)> _requests =
        Channel.CreateBounded<(TenantId, string)>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Request(TenantId tenant, string email)
    {
        if (!_requests.Writer.TryWrite((tenant, email)))
        {
            LogDropped(logger);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach ((TenantId tenant, string email) in _requests.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
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

                await scope.ServiceProvider.GetRequiredService<IEmailSender>()
                    .SendAsync(Compose(reset), stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failed reset must not stop the dispatcher for everyone else.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
    }

    private EmailMessage Compose(PasswordResetRequest reset)
    {
        Uri baseUrl = options.Value.PublicBaseUrl ?? throw new InvalidOperationException("Auth:PublicBaseUrl is not set.");
        string link = new Uri(baseUrl, $"/account/reset?token={Uri.EscapeDataString(reset.Token)}").AbsoluteUri;
        int minutes = (int)PasswordAccounts.ResetLifetime.TotalMinutes;

        // English until the interface is localised; the template set is per locale later.
        string text =
            $"""
             Someone asked to reset the password for the BrightTime account {reset.Email}.

             If it was you, open this link within {minutes} minutes to choose a new password:

             {link}

             If it wasn't you, you can ignore this email. Your password has not changed.
             """;
        string html =
            $"""
             <p>Someone asked to reset the password for the BrightTime account {WebUtility.HtmlEncode(reset.Email)}.</p>
             <p>If it was you, use this link within {minutes} minutes to choose a new password:</p>
             <p><a href="{WebUtility.HtmlEncode(link)}">Choose a new password</a></p>
             <p>If it wasn't you, you can ignore this email. Your password has not changed.</p>
             """;

        return new EmailMessage(reset.Email, "Reset your BrightTime password", html, text, Locale.Resolve(reset.Locale));
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Error, Message = "The password reset queue is full; a request was dropped.")]
    private static partial void LogDropped(ILogger logger);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A password reset failed: {Exception}.")]
    private static partial void LogFailed(ILogger logger, string exception);
}
