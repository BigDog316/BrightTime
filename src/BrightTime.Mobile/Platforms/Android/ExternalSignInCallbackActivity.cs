using Android.App;
using Android.Content;
using Android.Content.PM;

namespace BrightTime.Mobile;

/// <summary>
/// Receives the web's hand-back of an external sign-in (D41): <c>org.brighttime.app://external</c>,
/// the app's own scheme. MAUI's WebAuthenticator reads the code and state from it; the code is
/// useless without the PKCE verifier the app kept, so another app claiming the scheme gains nothing.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "org.brighttime.app",
    DataHost = "external")]
public sealed class ExternalSignInCallbackActivity : WebAuthenticatorCallbackActivity
{
}
