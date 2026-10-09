using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Abstractions.Messaging;

/// <summary>An outbound email. <paramref name="To"/> is a real address; it is hashed before it reaches <c>notify.delivery</c>.</summary>
public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody,
    Locale Locale,
    string? ReplyTo = null);

public interface IEmailSender
{
    /// <summary>Sends one message and returns the provider's reference for reconciliation.</summary>
    Task<DeliveryReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>What a provider tells us about an accepted send.</summary>
public sealed record DeliveryReceipt(bool Accepted, string? ProviderReference, string? ErrorCode = null);
