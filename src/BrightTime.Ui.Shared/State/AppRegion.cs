using BrightTime.Localisation;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.Ui.Shared.Api;

namespace BrightTime.Ui.Shared.State;

/// <summary>
/// The region this build of the app serves, as <see cref="Text"/> sees it: its languages and
/// time zone, from <c>GET /public/tenant</c>, and the language chosen on this device.
/// </summary>
/// <remarks>
/// The app has no wording overrides of its own (D34), so it renders the shipped catalogue.
/// Times are written in the region's zone, not the phone's, so a session reads the same on
/// every device and on the web.
/// </remarks>
public sealed class AppRegion : ITextContext
{
    private TenantInfo? _info;

    public TenantId TenantId => TenantId.None;

    public bool IsResolved => _info is not null;

    public Locale Locale { get; private set; } = Locale.Default;

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>The region's name, for the app bar; null before start-up.</summary>
    public string? Name => _info?.Name;

    /// <summary>The languages the region offers, in their own names.</summary>
    public IReadOnlyList<Locale> Locales { get; private set; } = Locale.Supported;

    /// <summary>Raised when the language changes, so every screen re-renders in it.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Settles the region and the language: the one chosen before, else the device's if the
    /// region offers it, else the region's default.
    /// </summary>
    public void Use(TenantInfo info, string? chosen, string? deviceLanguage)
    {
        ArgumentNullException.ThrowIfNull(info);
        _info = info;
        Locales = [.. info.Locales.Select(l => Locale.TryFromCode(l.Code, out Locale known) ? known : null).OfType<Locale>()];
        if (Locales.Count == 0)
        {
            Locales = [Locale.Default];
        }

        try
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(info.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            TimeZone = TimeZoneInfo.Utc;
        }

        Locale = Offered(chosen) ?? Offered(deviceLanguage) ?? Offered(info.DefaultLocale) ?? Locales[0];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Switches the language; false when the region does not offer it.</summary>
    public bool SetLocale(string code)
    {
        if (Offered(code) is not { } locale)
        {
            return false;
        }

        Locale = locale;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private Locale? Offered(string? tag) =>
        Locale.TryResolve(tag, out Locale found) && Locales.Contains(found) ? found : null;
}
