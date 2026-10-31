using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Abstractions.Payments;

public enum PaymentIntentState
{
    Created,
    RequiresAction,
    Processing,
    Succeeded,
    Failed,
    Cancelled,
    Refunded,
}

/// <summary>
/// A payment in flight. Note what is absent: no card number, no expiry, no
/// CVC, no cardholder name, and no secret for a card form. The card is
/// collected on the provider's own hosted page and we hold a reference, which
/// is what keeps this application out of PCI DSS scope beyond SAQ-A.
/// </summary>
public sealed record PaymentIntentDescriptor(
    string ProviderReference,
    PaymentIntentState State,
    Money Amount,
    Uri? HostedPaymentUrl);

public sealed record PaymentIntentRequest(
    Money Amount,
    string Description,
    string IdempotencyKey,
    Uri ReturnUrl,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// What a provider's webhook says happened to a payment: its own event id (so a replay is
/// recognised), the event's type, the payment's reference, and the state it is now in.
/// <paramref name="PaymentReference"/> is the reference the provider's later events will use, when
/// that isn't the one the payment began with (D74).
/// </summary>
public sealed record PaymentWebhookEvent(string EventId, string EventType, string ProviderReference, PaymentIntentState State, string? PaymentReference = null);

/// <summary>The provider refused or couldn't be reached. The message is for logs; a person is told the provider is unavailable.</summary>
public sealed class PaymentProviderException : Exception
{
    public PaymentProviderException()
    {
    }

    public PaymentProviderException(string message)
        : base(message)
    {
    }

    public PaymentProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IPaymentGateway
{
    string ProviderName { get; }

    /// <summary>The request header the provider signs its webhooks in; empty when it sends none.</summary>
    string WebhookSignatureHeader { get; }

    Task<PaymentIntentDescriptor> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken = default);

    Task<PaymentIntentDescriptor> GetIntentAsync(string providerReference, CancellationToken cancellationToken = default);

    Task<PaymentIntentDescriptor> RefundAsync(string providerReference, Money amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a webhook's signature and reads what it says. False when the signature doesn't
    /// verify, rather than throwing, so a forged callback is a 400 and not a 500. True with no
    /// event when the callback is genuine but about something a payment doesn't change.
    /// </summary>
    bool TryReadWebhook(ReadOnlySpan<byte> payload, string signatureHeader, out PaymentWebhookEvent? webhookEvent);
}
