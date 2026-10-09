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
/// CVC, no cardholder name. The card is collected by the provider's hosted
/// element and we hold a reference, which is what keeps this application out
/// of PCI DSS scope beyond SAQ-A.
/// </summary>
public sealed record PaymentIntentDescriptor(
    string ProviderReference,
    PaymentIntentState State,
    Money Amount,
    string? ClientSecret,
    Uri? HostedPaymentUrl);

public sealed record PaymentIntentRequest(
    Money Amount,
    string Description,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string>? Metadata = null);

public interface IPaymentGateway
{
    string ProviderName { get; }

    Task<PaymentIntentDescriptor> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken = default);

    Task<PaymentIntentDescriptor> GetIntentAsync(string providerReference, CancellationToken cancellationToken = default);

    Task<PaymentIntentDescriptor> RefundAsync(string providerReference, Money amount, string reason, CancellationToken cancellationToken = default);

    /// <summary>Verifies a webhook signature. Returns false rather than throwing, so a forged callback is a 400 and not a 500.</summary>
    bool TryVerifyWebhook(ReadOnlySpan<byte> payload, string signatureHeader, out string? eventType);
}
