using BrightTime.Abstractions.Payments;
using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Enrollment.Payments;

/// <summary>
/// What the payment provider reports (D56). A callback is trusted only once its signature
/// verifies, and then only queued: the payment changes in the worker, never in the anonymous
/// request (the actor guard on <c>enrollment.payment_intent</c> refuses a write with no one signed in).
/// </summary>
public sealed class PaymentWebhooks(EnrollmentDbContext db, ITenantContext tenant, IPaymentGateway gateway)
{
    /// <summary>Stripe's events are a few kilobytes; anything near this is not one of them.</summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>
    /// Verifies a callback and queues what it reports. Success(true) when queued, Success(false) when
    /// genuine but about nothing a payment changes; a provider other than the configured one is unknown.
    /// </summary>
    public Task<Result<bool>> AcceptAsync(string provider, byte[] payload, string signature, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!string.Equals(provider, gateway.ProviderName, StringComparison.Ordinal) || gateway.WebhookSignatureHeader.Length == 0)
        {
            return Task.FromResult(Result<bool>.Failure(Error.NotFound("payments.webhook_provider", "No payment provider takes callbacks here.")));
        }

        if (!gateway.TryReadWebhook(payload, signature ?? string.Empty, out PaymentWebhookEvent? reported))
        {
            return Task.FromResult(Result<bool>.Failure(Error.Validation("payments.webhook_signature", "The callback's signature doesn't verify.")));
        }

        if (reported is null)
        {
            return Task.FromResult(Result<bool>.Success(false));
        }

        return db.InTransactionAsync(async ct =>
        {
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = PaymentEventJob.JobType,
                TenantId = tenant.TenantId,
                Payload = new PaymentEventJob.Payload(gateway.ProviderName, reported.ProviderReference, PaymentIntents.Status(reported.State), reported.EventId, reported.PaymentReference),
                UniqueKey = $"{PaymentEventJob.JobType}:{gateway.ProviderName}:{reported.EventId}",
            }, ct).ConfigureAwait(false);
            return Result<bool>.Success(true);
        }, cancellationToken);
    }
}

/// <summary>
/// Moves a payment to what the provider reported. Providers deliver at least once and in any
/// order, so a status only moves forward: a late "processing" never undoes "succeeded", and a
/// waived or refunded payment is left as it is.
/// </summary>
public sealed class PaymentEventJob(EnrollmentDbContext db, IClock clock, ILogger<PaymentEventJob> logger) : IJobHandler
{
    public static string JobType => "enrollment.payment_event";

    /// <summary>
    /// The provider's reference and the reported status: nothing personal (the worker reads every
    /// tenant's jobs). <paramref name="PaymentRef"/> is the reference the provider uses from now on,
    /// when it names one (D74).
    /// </summary>
    public sealed record Payload(string Provider, string ProviderRef, string Status, string EventId, string? PaymentRef = null);

    public async Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = context.TenantId ?? throw new InvalidOperationException("A payment event must belong to a tenant.");
        Payload reported = context.ReadPayload<Payload>();

        await db.InTransactionAsync(async ct =>
        {
            // Either reference: the first session event moves the payment on to the intent's, and a later one still names the session.
            PaymentIntentRecord? intent = await db.PaymentIntents
                .FirstOrDefaultAsync(p => p.Provider == reported.Provider && (p.ProviderRef == reported.ProviderRef || (reported.PaymentRef != null && p.ProviderRef == reported.PaymentRef)), ct).ConfigureAwait(false);
            if (intent is null)
            {
                // Another tenant's, or made outside the platform: nothing here to change.
                PaymentLog.Unknown(logger, reported.EventId, reported.Status);
                return false;
            }

            bool linked = reported.PaymentRef is { } next && intent.ProviderRef != next;
            if (linked)
            {
                intent.ProviderRef = reported.PaymentRef;
            }

            if (!Moves(intent.Status, reported.Status))
            {
                if (linked)
                {
                    intent.UpdatedAt = clock.StoredNow();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                if (reported.Status == "succeeded" && intent.Status != "succeeded")
                {
                    // Paid after it was waived or cancelled: money was taken, so someone has to look.
                    PaymentLog.PaidWhenSettled(logger, intent.Id, intent.Status, reported.EventId);
                }

                return false;
            }

            intent.Status = reported.Status;
            intent.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a payment at <paramref name="current"/> moves to <paramref name="reported"/>.</summary>
    public static bool Moves(string current, string reported) => reported switch
    {
        "requires_action" => current is "created" or "processing",
        "processing" => current is "created" or "requires_action",
        "succeeded" => current is "created" or "requires_action" or "processing" or "failed",
        "failed" or "cancelled" => current is "created" or "requires_action" or "processing",
        "refunded" => current is "succeeded",
        _ => false,
    };
}

internal static partial class PaymentLog
{
    [LoggerMessage(EventId = 5601, Level = LogLevel.Information, Message = "Payment event {EventId} ({Status}) names no payment in this tenant.")]
    public static partial void Unknown(ILogger logger, string eventId, string status);

    [LoggerMessage(EventId = 5602, Level = LogLevel.Warning, Message = "Payment {PaymentId} was {Status} when event {EventId} reported it paid; it needs a refund or a correction.")]
    public static partial void PaidWhenSettled(ILogger logger, Guid paymentId, string status, string eventId);

    [LoggerMessage(EventId = 5603, Level = LogLevel.Warning, Message = "The payment provider failed: {Reason}")]
    public static partial void ProviderFailed(ILogger logger, string reason, Exception exception);
}

public static class PaymentJobs
{
    /// <summary>The worker's side of payments. The host registers <see cref="EnrollmentDbContext"/> on its own login.</summary>
    public static IServiceCollection AddPaymentJobs(this IServiceCollection services)
    {
        services.AddJobHandler<PaymentEventJob>();
        return services;
    }
}
