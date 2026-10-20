using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrightTime.Abstractions.Security;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Notifications.Contracts;
using BrightTime.Modules.Notifications.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Messaging;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Notifications;

public sealed record DeviceView(Guid Id, string Platform, DateTimeOffset RegisteredAt);

public sealed record ChannelChoice(string Kind, string Channel, bool Enabled);

/// <summary>A person's notification settings: one quiet window, and which kinds arrive where.</summary>
public sealed record NotificationSettings(TimeOnly? QuietFrom, TimeOnly? QuietTo, IReadOnlyList<ChannelChoice> Choices);

public sealed record InboxItem(Guid Id, string Kind, string TitleKey, IReadOnlyDictionary<string, string> Arguments, string? DeepLink, DateTimeOffset CreatedAt, bool IsRead);

/// <summary>One push to one device, already in the words its phone was registered in.</summary>
public sealed record PushMessage(string Platform, string Token, string Kind, string Title, string Body, string? DeepLink)
{
    /// <summary>Never the token or the words: a logged message must not carry either.</summary>
    public override string ToString() => $"PushMessage {{ Platform = {Platform}, Kind = {Kind} }}";
}

/// <summary>Sends a push to one device. Firebase and APNs adapters arrive with their accounts; until then pushes are logged.</summary>
public interface IPushSender
{
    Task<bool> SendAsync(PushMessage message, CancellationToken cancellationToken);
}

/// <summary>Logs that a push would go, and to which platform: never the token, never the content.</summary>
public sealed partial class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task<bool> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        LogPush(logger, message.Kind, message.Platform);
        return Task.FromResult(true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Push ({Kind}) to a {Platform} device; no push provider is configured, so nothing was sent.")]
    private static partial void LogPush(ILogger logger, string kind, string platform);
}

/// <summary>Registers and forgets the phones a person may be pushed to.</summary>
public sealed class PushDevices(NotificationsDbContext db, IFieldCipher cipher, HmacTokenHasher hasher, ITenantContext tenant, IClock clock)
{
    public const string TokenPurpose = "push-token";
    public const int MaxTokenLength = 4096;
    private static readonly HashSet<string> Platforms = new(["ios", "android", "web"], StringComparer.Ordinal);

    /// <summary>
    /// Registers a device for the person, or takes it over if another account had it: a phone
    /// passed down in a family must stop receiving its previous owner's notifications.
    /// </summary>
    public Task<Result<DeviceView>> RegisterAsync(UserId user, string platform, string token, string? appVersion, string? osVersion, string? locale, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!Platforms.Contains(platform ?? string.Empty))
            {
                return Result<DeviceView>.Failure(Error.Validation("devices.platform", "One of ios, android, web."));
            }

            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
            {
                return Result<DeviceView>.Failure(Error.Validation("devices.token", "A push token from the platform."));
            }

            byte[] hash = await hasher.HashAsync(tenant.TenantId, TokenPurpose, token, ct).ConfigureAwait(false);
            byte[] sealedToken = (await cipher.EncryptAsync(tenant.TenantId, token, ct).ConfigureAwait(false))!;
            DateTimeOffset now = clock.UtcNow;

            PushDeviceRecord? device = await db.PushDevices.FirstOrDefaultAsync(d => d.TokenHash == hash, ct).ConfigureAwait(false);
            if (device is null)
            {
                device = new PushDeviceRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    UserId = user.Value,
                    PlatformKind = platform!,
                    TokenHash = hash,
                    TokenEnc = sealedToken,
                };
                db.PushDevices.Add(device);
            }

            device.UserId = user.Value;
            device.PlatformKind = platform!;
            device.TokenEnc = sealedToken;
            device.AppVersion = Clip(appVersion, 40);
            device.OsVersion = Clip(osVersion, 40);
            device.Locale = Clip(locale, 16);
            device.RegisteredAt = now;
            device.LastSeenAt = now;
            device.RevokedAt = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<DeviceView>.Success(new DeviceView(device.Id, device.PlatformKind, device.RegisteredAt));
        }, cancellationToken);

    /// <summary>Stops pushes to one of the person's devices. True when it was theirs and open.</summary>
    public Task<bool> UnregisterAsync(UserId user, Guid deviceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            PushDeviceRecord? device = await db.PushDevices.FirstOrDefaultAsync(d => d.Id == deviceId && d.UserId == user.Value && d.RevokedAt == null, ct).ConfigureAwait(false);
            if (device is null)
            {
                return false;
            }

            device.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static string? Clip(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim() is { } v && v.Length > length ? v[..length] : value.Trim();
}

/// <summary>Which kinds of notification arrive on which channels, and when to stay quiet.</summary>
public sealed class NotificationPreferences(NotificationsDbContext db, ITenantContext tenant)
{
    /// <summary>Every choice the person can make, with theirs applied over the defaults (everything on).</summary>
    public Task<NotificationSettings> GetAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            List<PreferenceRecord> mine = await db.Preferences.AsNoTracking().Where(p => p.UserId == user.Value).ToListAsync(ct).ConfigureAwait(false);
            return Settings(mine);
        }, cancellationToken);

    public Task<Result<NotificationSettings>> SetAsync(UserId user, NotificationSettings settings, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(settings);
            if ((settings.QuietFrom is null) != (settings.QuietTo is null))
            {
                return Result<NotificationSettings>.Failure(Error.Validation("preferences.quiet_hours", "Give both ends of the quiet hours, or neither."));
            }

            foreach (ChannelChoice choice in settings.Choices)
            {
                if (!NotificationKinds.UserConfigurable.Contains(choice.Kind) || !NotificationChannels.UserConfigurable.Contains(choice.Channel))
                {
                    return Result<NotificationSettings>.Failure(Error.Validation("preferences.unknown_choice",
                        $"Kinds: {string.Join(", ", NotificationKinds.UserConfigurable)}. Channels: {string.Join(", ", NotificationChannels.UserConfigurable)}."));
                }
            }

            List<PreferenceRecord> mine = await db.Preferences.Where(p => p.UserId == user.Value).ToListAsync(ct).ConfigureAwait(false);
            Dictionary<(string, string), bool> asked = settings.Choices.ToDictionary(c => (c.Kind, c.Channel), c => c.Enabled);
            foreach (string kind in NotificationKinds.UserConfigurable)
            {
                foreach (string channel in NotificationChannels.UserConfigurable)
                {
                    PreferenceRecord? row = mine.FirstOrDefault(p => p.NotificationKind == kind && p.Channel == channel);
                    if (row is null)
                    {
                        row = new PreferenceRecord { UserId = user.Value, TenantId = tenant.TenantId.Value, NotificationKind = kind, Channel = channel };
                        db.Preferences.Add(row);
                        mine.Add(row);
                    }

                    if (asked.TryGetValue((kind, channel), out bool enabled))
                    {
                        row.IsEnabled = enabled;
                    }

                    // One quiet window for everything that can wake a phone; email and the inbox don't.
                    row.QuietFromLocal = channel == NotificationChannels.Push ? settings.QuietFrom : null;
                    row.QuietToLocal = channel == NotificationChannels.Push ? settings.QuietTo : null;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<NotificationSettings>.Success(Settings(mine));
        }, cancellationToken);

    private static NotificationSettings Settings(IReadOnlyList<PreferenceRecord> mine)
    {
        PreferenceRecord? push = mine.FirstOrDefault(p => p.Channel == NotificationChannels.Push && p.QuietFromLocal is not null);
        return new NotificationSettings(
            push?.QuietFromLocal,
            push?.QuietToLocal,
            [.. from kind in NotificationKinds.UserConfigurable
                from channel in NotificationChannels.UserConfigurable
                select new ChannelChoice(kind, channel,
                    mine.FirstOrDefault(p => p.NotificationKind == kind && p.Channel == channel)?.IsEnabled ?? true)]);
    }
}

/// <summary>The person's inbox: what they were told, newest first.</summary>
public sealed class NotificationInbox(NotificationsDbContext db, IClock clock)
{
    public const int PageSize = 50;

    public Task<IReadOnlyList<InboxItem>> ListAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<InboxItem>>(async ct =>
        {
            DateTimeOffset now = clock.UtcNow;
            List<NotificationRecord> rows = await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == user.Value && n.DismissedAt == null && (n.ExpiresAt == null || n.ExpiresAt > now))
                .OrderByDescending(n => n.CreatedAt)
                .Take(PageSize)
                .ToListAsync(ct).ConfigureAwait(false);
            return [.. rows.Select(n => new InboxItem(n.Id, n.NotificationKind, n.Title, Arguments(n.Payload), n.DeepLink, n.CreatedAt, n.ReadAt is not null))];
        }, cancellationToken);

    public Task<bool> MarkReadAsync(UserId user, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.Notifications.Where(n => n.Id == id && n.UserId == user.Value && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, clock.UtcNow), ct).ConfigureAwait(false) > 0, cancellationToken);

    internal static IReadOnlyDictionary<string, string> Arguments(string payload)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Object
                ? args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal)
                : new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>
/// Delivers <see cref="NotificationRequested"/>: the inbox always, unless the person switched the
/// kind off there; a push to each open device when push is on, outside quiet hours, and the
/// account may be messaged at all.
/// </summary>
/// <remarks>
/// Idempotent: the notification's id is derived from the tenant and the request's dedupe key, so
/// a retried delivery inserts nothing and pushes nothing a second time.
/// </remarks>
public sealed class NotificationRequestedHandler(
    NotificationsDbContext db,
    ITenantContext tenant,
    IIdentityQueries identity,
    IFieldCipher cipher,
    NotificationTextFactory words,
    IPushSender push,
    IClock clock) : IModuleMessageHandler<NotificationRequested>
{
    public async Task HandleAsync(NotificationRequested message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        Guid id = IdFor(tenant.TenantId.Value, message.DedupeKey);

        (bool created, List<PushDeviceRecord> devices) = await db.InTransactionAsync(async ct =>
        {
            if (await db.Notifications.AnyAsync(n => n.Id == id, ct).ConfigureAwait(false))
            {
                return (false, new List<PushDeviceRecord>());
            }

            List<PreferenceRecord> prefs = await db.Preferences.AsNoTracking()
                .Where(p => p.UserId == message.UserId && p.NotificationKind == message.Kind)
                .ToListAsync(ct).ConfigureAwait(false);
            bool On(string channel) => prefs.FirstOrDefault(p => p.Channel == channel)?.IsEnabled ?? true;

            if (On(NotificationChannels.InApp))
            {
                db.Notifications.Add(new NotificationRecord
                {
                    Id = id,
                    TenantId = tenant.TenantId.Value,
                    UserId = message.UserId,
                    NotificationKind = message.Kind,
                    Title = message.TitleKey,
                    DeepLink = message.DeepLink,
                    Payload = JsonSerializer.Serialize(new { args = message.Arguments, dedupe = message.DedupeKey }),
                    CreatedAt = clock.UtcNow,
                    ExpiresAt = message.ExpiresAt,
                });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            PreferenceRecord? pushPref = prefs.FirstOrDefault(p => p.Channel == NotificationChannels.Push);
            if (!On(NotificationChannels.Push) || InQuietHours(pushPref, tenant.TimeZone, clock.UtcNow))
            {
                return (true, new List<PushDeviceRecord>());
            }

            List<PushDeviceRecord> open = await db.PushDevices.AsNoTracking()
                .Where(d => d.UserId == message.UserId && d.RevokedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            return (true, open);
        }, cancellationToken).ConfigureAwait(false);

        if (!created || devices.Count == 0)
        {
            return;
        }

        // A child profile has no phone of its own, and an account that may not be messaged gets
        // no push: the capability says so for under-13 accounts (D31).
        YouthCapabilities can = await identity.GetCapabilitiesAsync(new UserId(message.UserId), cancellationToken).ConfigureAwait(false);
        if (!can.HasAccountConsent)
        {
            return;
        }

        foreach (PushDeviceRecord device in devices)
        {
            string? token = await cipher.DecryptAsync(tenant.TenantId, device.TokenEnc, cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                continue;
            }

            // In the phone's language, which may not be the language of whoever else uses the account.
            Text text = await words.ForAsync(device.Locale, cancellationToken).ConfigureAwait(false);
            NotificationWords said = NotificationText.Render(text, message.TitleKey, message.Arguments);
            await push.SendAsync(new PushMessage(device.PlatformKind, token, message.Kind, said.Title, said.Body, message.DeepLink), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A stable id per tenant and dedupe key: version 8, from SHA-256.</summary>
    internal static Guid IdFor(Guid tenantId, string dedupeKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId.ToString("N") + "\n" + dedupeKey));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>Whether now, in the tenant's zone, falls inside the person's quiet window (which may cross midnight).</summary>
    internal static bool InQuietHours(PreferenceRecord? pref, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (pref is not { QuietFromLocal: { } from, QuietToLocal: { } to } || from == to)
        {
            return false;
        }

        TimeOnly local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        return from < to ? local >= from && local < to : local >= from || local < to;
    }
}

/// <summary>Registration of the Notifications module.</summary>
public static class NotificationsModule
{
    /// <summary>The request-side services: devices, preferences and the inbox.</summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services)
    {
        services.AddScoped<PushDevices>();
        services.AddScoped<NotificationPreferences>();
        services.AddScoped<NotificationInbox>();
        return services;
    }

    /// <summary>The worker side: delivering requested notifications. Needs the shared catalogue and wording overrides.</summary>
    public static IServiceCollection AddNotificationDelivery(this IServiceCollection services)
    {
        services.AddScoped<NotificationTextFactory>();
        services.AddSingleton<IPushSender, LoggingPushSender>();
        services.AddMessageHandler<NotificationRequested, NotificationRequestedHandler>();
        return services;
    }
}
