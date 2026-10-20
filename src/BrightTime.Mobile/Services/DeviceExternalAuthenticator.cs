using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Mobile.Services;

/// <summary>
/// External sign-in through the system browser (D41; RFC 8252): a Custom Tab on Android, an
/// authentication session on iOS. The browser keeps the provider's cookies away from the app, and
/// an ephemeral session on iOS keeps a shared device from staying signed in to the provider.
/// </summary>
internal sealed class DeviceExternalAuthenticator : IExternalAuthenticator
{
    public bool IsSupported => true;

    public async Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri start, Uri callback, CancellationToken cancellationToken = default)
    {
        try
        {
            WebAuthenticatorResult result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = start,
                CallbackUrl = callback,
                PrefersEphemeralWebBrowserSession = true,
            }, cancellationToken);
            return result.Properties;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }
}
