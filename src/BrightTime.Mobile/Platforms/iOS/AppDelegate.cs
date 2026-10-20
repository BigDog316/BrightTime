using Foundation;
using UIKit;

namespace BrightTime.Mobile;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

/// <summary>
/// Universal links (D35) arrive at the scene, as a browsing activity, while the app runs or when
/// it starts from one; each is handed to <see cref="App"/>. Built and checked only on a Mac.
/// </summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : MauiUISceneDelegate
{
    public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        base.WillConnect(scene, session, connectionOptions);
        foreach (NSUserActivity activity in connectionOptions.UserActivities)
        {
            Open(activity);
        }
    }

    public override void ContinueUserActivity(UIScene scene, NSUserActivity userActivity)
    {
        base.ContinueUserActivity(scene, userActivity);
        Open(userActivity);
    }

    private static void Open(NSUserActivity activity)
    {
        if (activity.ActivityType == NSUserActivityType.BrowsingWeb
            && activity.WebPageUrl?.AbsoluteString is { } data
            && Uri.TryCreate(data, UriKind.Absolute, out Uri? link))
        {
            Microsoft.Maui.Controls.Application.Current?.SendOnAppLinkRequestReceived(link);
        }
    }
}
