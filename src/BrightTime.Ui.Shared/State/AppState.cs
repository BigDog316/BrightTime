using System.Globalization;
using System.Text.Json;
using BrightTime.Localisation;
using BrightTime.Ui.Shared.Api;
using BrightTime.Ui.Shared.Platform;

namespace BrightTime.Ui.Shared.State;

/// <summary>Start-up, and the few settings the whole app shares.</summary>
/// <remarks>
/// The region's details are kept on the device after the first start, so the app opens
/// offline in the right language and shows what it kept (saved items) instead of failing.
/// </remarks>
public sealed class AppState(BrightTimeApi api, AppSession session, AppRegion region, Text text, IOfflineStore offline, IAppPreferences preferences)
{
    public const string LocaleKey = "brighttime.locale";
    public const string TenantCacheKey = "tenant";

    /// <summary>How long an under-13 answer to the age question is remembered on this device.</summary>
    public static readonly TimeSpan AgeAnswerMemory = TimeSpan.FromDays(1);

    private const string AgeAnswerKey = "brighttime.age_under_13_at";

    /// <summary>True once the region is known and any kept sign-in restored.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Why start-up could not finish: offline on the very first start.</summary>
    public ApiProblem? StartProblem { get; private set; }

    public event EventHandler? Changed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartProblem = null;
        ApiResult<TenantInfo> fetched = await api.TenantAsync(cancellationToken).ConfigureAwait(false);
        TenantInfo? info = fetched.Value;
        if (fetched.IsSuccess)
        {
            await offline.SaveAsync(TenantCacheKey, JsonSerializer.Serialize(info, AppSession.Json)).ConfigureAwait(false);
        }
        else if (await offline.LoadAsync(TenantCacheKey).ConfigureAwait(false) is { } kept)
        {
            info = JsonSerializer.Deserialize<TenantInfo>(kept, AppSession.Json);
        }

        if (info is null)
        {
            StartProblem = fetched.Problem;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        region.Use(info, preferences.Get(LocaleKey), CultureInfo.CurrentUICulture.Name);
        await text.LoadAsync(cancellationToken).ConfigureAwait(false);
        await session.RestoreAsync(cancellationToken).ConfigureAwait(false);
        IsReady = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Chooses the app's language, kept for next time.</summary>
    public void ChooseLocale(string code)
    {
        if (region.SetLocale(code))
        {
            preferences.Set(LocaleKey, region.Locale.Code);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Remembers that the age question was answered under 13, so going back and changing the
    /// answer does not get past it at once (D35; the FTC's neutral age screen).
    /// </summary>
    public void RememberUnder13(DateTimeOffset now) =>
        preferences.Set(AgeAnswerKey, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    public bool AnsweredUnder13Recently(DateTimeOffset now) =>
        long.TryParse(preferences.Get(AgeAnswerKey), NumberStyles.None, CultureInfo.InvariantCulture, out long at)
        && now - DateTimeOffset.FromUnixTimeSeconds(at) < AgeAnswerMemory;
}
