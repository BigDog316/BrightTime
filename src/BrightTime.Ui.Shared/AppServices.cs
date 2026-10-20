using BrightTime.Localisation;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;
using BrightTime.Ui.Shared.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BrightTime.Ui.Shared;

/// <summary>Where this build of the app points: the region's API, and its web pages.</summary>
/// <param name="ApiBase">The region's API, ending <c>/api/v1/</c>.</param>
/// <param name="SiteBase">The region's web address, for pages the app opens outside itself.</param>
public sealed record AppEndpoints(Uri ApiBase, Uri SiteBase)
{
    /// <summary>A listing's calendar file, which the phone's calendar opens.</summary>
    public Uri Calendar(string slug) => new(SiteBase, $"/listings/{Uri.EscapeDataString(slug)}/calendar.ics");

    /// <summary>A listing's web page, for sharing with someone who has no app.</summary>
    public Uri Listing(string slug) => new(SiteBase, $"/listings/{Uri.EscapeDataString(slug)}");
}

public static class AppServices
{
    /// <summary>
    /// The shared app: the API client, the session, the region and its words, and in-memory
    /// stand-ins for the device services, which a host replaces by registering its own first.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="endpoints">The region's API and web addresses.</param>
    /// <param name="http">Makes the client for the API. The host chooses the handler (and, in development, the Host header).</param>
    public static IServiceCollection AddBrightTimeApp(this IServiceCollection services, AppEndpoints endpoints, Func<IServiceProvider, HttpClient> http)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(http);

        services.AddSingleton(endpoints);
        services.AddSingleton(sp =>
        {
            HttpClient client = http(sp);
            client.BaseAddress ??= endpoints.ApiBase;
            return new ApiHttp(client);
        });
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<ISecretStore, MemorySecretStore>();
        services.TryAddSingleton<IAppPreferences, MemoryPreferences>();
        services.TryAddSingleton<IOfflineStore, MemoryOfflineStore>();
        services.TryAddSingleton<ILocationProvider, NoLocation>();
        services.TryAddSingleton<IPushRegistrar, NoPush>();
        services.TryAddSingleton<IPasskeyAuthenticator, NoPasskeys>();
        services.TryAddSingleton<IShareService, NoShare>();
        services.TryAddSingleton<IExternalLauncher, NoLauncher>();

        services.AddSingleton<AppRegion>();
        services.AddSingleton<ITextContext>(sp => sp.GetRequiredService<AppRegion>());
        services.AddSingleton(_ => Catalogue.Load());
        services.TryAddSingleton<IWordingOverrides, NoWordingOverrides>();
        services.AddSingleton<Text>();

        services.AddSingleton(sp => new AppSession(sp.GetRequiredService<ApiHttp>().Client, sp.GetRequiredService<ISecretStore>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new BrightTimeApi(sp.GetRequiredService<ApiHttp>().Client, sp.GetRequiredService<AppSession>(), sp.GetRequiredService<ITextContext>()));
        services.AddSingleton<AppState>();
        services.AddSingleton<AppLinks>();
        services.AddSingleton<PendingConsent>();
        return services;
    }

    /// <summary>The one client the app talks to its API with, so its handler is shared.</summary>
    internal sealed record ApiHttp(HttpClient Client);

    private sealed class NoShare : IShareService
    {
        public Task ShareAsync(string title, Uri link) => Task.CompletedTask;
    }

    private sealed class NoLauncher : IExternalLauncher
    {
        public Task OpenAsync(Uri link) => Task.CompletedTask;
    }
}
