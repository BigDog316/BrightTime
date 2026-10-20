using BrightTime.Mobile.Services;
using BrightTime.Ui.Shared;
using BrightTime.Ui.Shared.Platform;
using Microsoft.Extensions.Logging;

namespace BrightTime.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // The device's side of the shared UI (docs/plan/01, "Mobile sharing model").
        builder.Services.AddSingleton<ISecretStore, DeviceSecretStore>();
        builder.Services.AddSingleton<IAppPreferences, DevicePreferences>();
        builder.Services.AddSingleton<IOfflineStore, DeviceOfflineStore>();
        builder.Services.AddSingleton<ILocationProvider, DeviceLocation>();
        builder.Services.AddSingleton<IShareService, DeviceShare>();
        builder.Services.AddSingleton<IExternalLauncher, DeviceLauncher>();
        builder.Services.AddSingleton<IExternalAuthenticator, DeviceExternalAuthenticator>();
#if ANDROID
        builder.Services.AddSingleton<IPasskeyAuthenticator, AndroidPasskeys>();
#endif

        builder.Services.AddBrightTimeApp(
            new AppEndpoints(new Uri(BuildConfig.ApiBase), new Uri(BuildConfig.SiteBase)),
            _ => ApiClient.Create());

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif

        return builder.Build();
    }
}
