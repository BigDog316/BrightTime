using BrightTime.Ui.Shared.State;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace BrightTime.Mobile;

/// <summary>
/// One window, one BlazorWebView over the shared UI's <see cref="Ui.Shared.Routes"/>. Links the
/// system opens the app with (D35's <c>/app/*</c>) are handed to <see cref="AppLinks"/>.
/// </summary>
public sealed class App(AppLinks links) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new ContentPage
        {
            Content = new BlazorWebView
            {
                HostPage = "wwwroot/index.html",
                RootComponents = { new RootComponent { Selector = "#app", ComponentType = typeof(Ui.Shared.Routes) } },
            },
        })
        {
            Title = "BrightTime",
        };

    protected override void OnAppLinkRequestReceived(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        base.OnAppLinkRequestReceived(uri);

        // Only this region's own links; anything else the system sends is ignored.
        if (string.Equals(uri.Host, BuildConfig.LinkHost, StringComparison.OrdinalIgnoreCase))
        {
            links.Open(uri);
        }
    }
}
