using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Abstractions.Messaging;

public enum PushPlatform
{
    Ios,
    Android,
    Web,
}

/// <summary>
/// A push notification. Same rule as SMS: the payload is a title, a short
/// prompt and a deep link. Anything identifying stays behind authentication in
/// the app.
/// </summary>
public sealed record PushMessage(
    string DeviceToken,
    PushPlatform Platform,
    string Title,
    string Body,
    string? DeepLink,
    Locale Locale,
    IReadOnlyDictionary<string, string>? Data = null);

public interface IPushSender
{
    Task<DeliveryReceipt> SendAsync(PushMessage message, CancellationToken cancellationToken = default);

    /// <summary>Reports tokens the provider has rejected as permanently invalid, so they can be revoked.</summary>
    Task<IReadOnlyList<string>> SendBatchAsync(IReadOnlyList<PushMessage> messages, CancellationToken cancellationToken = default);
}
