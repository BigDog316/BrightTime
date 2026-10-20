using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace BrightTime.Mobile;

/// <summary>
/// The app's one activity. It claims the region's <c>/app/</c> links (D35), verified against the
/// site's <c>/.well-known/assetlinks.json</c>, so a consent email's link opens here, and hands
/// each to <see cref="App"/>: on a cold start, and to the running app (single top).
/// </summary>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https",
    DataHost = BuildConfig.LinkHost,
    DataPathPrefix = "/app/",
    AutoVerify = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Open(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Open(intent);
    }

    // MAUI does not pass view intents on by itself.
    private static void Open(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView
            && intent.DataString is { } data
            && Uri.TryCreate(data, UriKind.Absolute, out Uri? link))
        {
            Microsoft.Maui.Controls.Application.Current?.SendOnAppLinkRequestReceived(link);
        }
    }
}
