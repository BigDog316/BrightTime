using BrightTime.Abstractions.Payments;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Enrollment.Payments;

/// <summary>
/// Payment outside the platform (D55): cash, a cheque, a transfer the organisation receives
/// itself. The intent waits, needing action, until an admin of the organisation records the money
/// as received. No card, no provider, no webhook. It is also the gateway until a provider is
/// configured (Q6).
/// </summary>
public sealed class ManualPaymentGateway : IPaymentGateway
{
    public const string Name = "manual";

    public string ProviderName => Name;

    public string WebhookSignatureHeader => string.Empty;

    public Task<PaymentIntentDescriptor> CreateIntentAsync(PaymentIntentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new PaymentIntentDescriptor("manual_" + Guid.NewGuid().ToString("N"), PaymentIntentState.RequiresAction, request.Amount, null));
    }

    public Task<PaymentIntentDescriptor> GetIntentAsync(string providerReference, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentIntentDescriptor(providerReference, PaymentIntentState.RequiresAction, Money.Zero, null));

    public Task<PaymentIntentDescriptor> RefundAsync(string providerReference, Money amount, string reason, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentIntentDescriptor(providerReference, PaymentIntentState.Refunded, amount, null));

    /// <summary>A manual payment has no provider to call back, so no webhook is ever genuine.</summary>
    public bool TryReadWebhook(ReadOnlySpan<byte> payload, string signatureHeader, out PaymentWebhookEvent? webhookEvent)
    {
        webhookEvent = null;
        return false;
    }
}
