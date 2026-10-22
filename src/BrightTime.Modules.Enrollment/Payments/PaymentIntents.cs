using BrightTime.Abstractions.Payments;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BrightTime.Modules.Enrollment.Payments;

/// <summary>
/// A payment as the family or the organisation sees it. <see cref="ClientSecret"/> and
/// <see cref="HostedPaymentUrl"/> come from the provider when the payment is made, so the card is
/// entered on the provider's page, never ours (T20); neither is kept.
/// </summary>
public sealed record PaymentView(
    Guid Id,
    Guid? ApplicationId,
    Guid? EnrollmentId,
    Guid? FeeScheduleId,
    int AmountCents,
    string Currency,
    string Provider,
    string Status,
    string? WaivedReason,
    DateTimeOffset CreatedAt,
    string? ClientSecret = null,
    Uri? HostedPaymentUrl = null);

/// <summary>
/// Payments for places (M3b, D55): a family pays a listing's fee, at a sliding-scale tier it
/// fits, through whichever provider is configured (<see cref="IPaymentGateway"/>; manual until Q6).
/// </summary>
/// <remarks>
/// <para>
/// Only the adult who owns the family profile pays, for an application that was accepted or
/// offered a seat, or an active enrolment (docs/plan/04: <c>payments.initiate</c> is for adults).
/// Asking again while a payment is still open gives that payment back rather than a second one.
/// </para>
/// <para>
/// No card data is ever held: an amount, a status and the provider's reference (T20). An
/// organisation's admins waive a fee with a reason, or record money received outside the platform
/// for a manual payment; both are audited.
/// </para>
/// </remarks>
public sealed class PaymentIntents(
    EnrollmentDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IListingDetails listings,
    IPaymentGateway gateway,
    ILogger<PaymentIntents> logger)
{
    public const string Waived = "enrollment.payment.waived";
    public const string Received = "enrollment.payment.received";

    private static readonly string[] Open = ["created", "requires_action", "processing"];

    /// <summary>Starts paying a fee, at a tier if one is chosen; an open payment for the same place is returned instead.</summary>
    public Task<Result<PaymentView>> CreateAsync(UserId owner, bool ownerIsAdult, Guid? applicationId, Guid? enrollmentId, Guid feeScheduleId, Guid? tierId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!ownerIsAdult)
            {
                return Result<PaymentView>.Failure(Error.Forbidden("payments.adult", "An adult pays fees."));
            }

            if (idempotencyKey is not { Length: >= 8 and <= 100 })
            {
                return Result<PaymentView>.Failure(Error.Validation("application.idempotency_key", "An Idempotency-Key header of 8 to 100 characters."));
            }

            if (await PlaceAsync(owner, applicationId, enrollmentId, ct).ConfigureAwait(false) is not { } place)
            {
                return Result<PaymentView>.Failure(Error.NotFound("payments.place", "None of your family's accepted applications or enrolments has that id."));
            }

            FeeScheduleRecord? fee = await db.FeeSchedules.AsNoTracking().FirstOrDefaultAsync(f => f.Id == feeScheduleId && f.ListingId == place.ListingId, ct).ConfigureAwait(false);
            if (fee is null)
            {
                return Result<PaymentView>.Failure(Error.Validation("payments.fee", "One of this activity's fees."));
            }

            int amount = fee.AmountCents;
            if (tierId is { } chosen)
            {
                SlidingScaleTierRecord? tier = await db.SlidingScaleTiers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == chosen && t.FeeScheduleId == fee.Id, ct).ConfigureAwait(false);
                if (tier is null || !FeeSchedules.Fits(new TierView(tier.Id, tier.Label, tier.AmountCents, tier.HouseholdSizeMin, tier.HouseholdSizeMax, tier.IncomeBand), place.HouseholdSize))
                {
                    return Result<PaymentView>.Failure(Error.Validation("payments.tier", "A tier of this fee that fits your household."));
                }

                amount = tier.AmountCents;
            }

            if (await db.PaymentIntents.FirstOrDefaultAsync(p => (p.ApplicationId == place.ApplicationId || (place.EnrollmentId != null && p.EnrollmentId == place.EnrollmentId))
                    && Open.Contains(p.Status), ct).ConfigureAwait(false) is { } open)
            {
                if (open.Provider == ManualPaymentGateway.Name || open.Provider != gateway.ProviderName)
                {
                    return Result<PaymentView>.Success(View(open));
                }

                // The client secret isn't kept (T20), so the provider is asked for it again.
                PaymentIntentDescriptor? again = open.ProviderRef is null ? null : await ProviderAsync(() => gateway.GetIntentAsync(open.ProviderRef, ct)).ConfigureAwait(false);
                return again is null
                    ? Result<PaymentView>.Failure(Unavailable())
                    : Result<PaymentView>.Success(View(open) with { ClientSecret = again.ClientSecret, HostedPaymentUrl = again.HostedPaymentUrl });
            }

            if (amount == 0)
            {
                return Result<PaymentView>.Failure(Error.Validation("payments.free", "There is nothing to pay."));
            }

            // The provider's idempotency keys span its whole account, so the family's key is scoped to the tenant and the adult.
            PaymentIntentDescriptor? made = await ProviderAsync(() => gateway.CreateIntentAsync(
                new PaymentIntentRequest(Money.FromCents(amount, fee.Currency), fee.Label, $"{tenant.TenantId.Value:N}-{owner.Value:N}-{idempotencyKey}",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["tenant"] = tenant.TenantId.Value.ToString("N") }), ct)).ConfigureAwait(false);
            if (made is null)
            {
                return Result<PaymentView>.Failure(Unavailable());
            }

            DateTimeOffset now = clock.StoredNow();
            PaymentIntentRecord intent = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                ApplicationId = place.ApplicationId,
                EnrollmentId = place.EnrollmentId,
                FeeScheduleId = fee.Id,
                AmountCents = amount,
                Currency = fee.Currency,
                Provider = gateway.ProviderName,
                ProviderRef = made.ProviderReference,
                Status = Status(made.State),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PaymentIntents.Add(intent);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<PaymentView>.Success(View(intent) with { ClientSecret = made.ClientSecret, HostedPaymentUrl = made.HostedPaymentUrl });
        }, cancellationToken);

    /// <summary>A payment, for the family that made it.</summary>
    public Task<Result<PaymentView>> GetAsync(UserId owner, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnAsync(owner, id, ct).ConfigureAwait(false) is { } intent
                ? Result<PaymentView>.Success(View(intent))
                : Result<PaymentView>.Failure(NotFound()), cancellationToken);

    /// <summary>The payments for an organisation's listing, newest first.</summary>
    public Task<Result<IReadOnlyList<PaymentView>>> ForListingAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if ((await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId != org)
            {
                return Result<IReadOnlyList<PaymentView>>.Failure(Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id."));
            }

            List<PaymentIntentRecord> intents = await (from p in db.PaymentIntents.AsNoTracking()
                                                       join a in db.Applications on p.ApplicationId equals a.Id
                                                       where a.ListingId == listingId
                                                       orderby p.CreatedAt descending
                                                       select p).Take(500).ToListAsync(ct).ConfigureAwait(false);
            return Result<IReadOnlyList<PaymentView>>.Success([.. intents.Select(View)]);
        }, cancellationToken);

    /// <summary>Waives a payment not yet made, with the reason (<c>ck_payment_waived</c> insists on one).</summary>
    public Task<Result<PaymentView>> WaiveAsync(OrganisationId org, Guid id, string reason, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? why = reason?.Trim();
            if (why is not { Length: > 0 and <= 200 })
            {
                return Result<PaymentView>.Failure(Error.Validation("payments.reason", "Say why, in up to 200 characters."));
            }

            if (await OrganisationsAsync(org, id, ct).ConfigureAwait(false) is not { } intent)
            {
                return Result<PaymentView>.Failure(NotFound());
            }

            if (!Open.Contains(intent.Status))
            {
                return Result<PaymentView>.Failure(Error.Conflict("payments.settled", "This payment is already settled."));
            }

            intent.Status = "waived";
            intent.WaivedReason = why;
            intent.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Waived, org, intent.Id, why, ct).ConfigureAwait(false);
            return Result<PaymentView>.Success(View(intent));
        }, cancellationToken);

    /// <summary>Records a manual payment as received: cash, a cheque, a transfer the organisation took itself.</summary>
    public Task<Result<PaymentView>> RecordReceivedAsync(OrganisationId org, Guid id, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OrganisationsAsync(org, id, ct).ConfigureAwait(false) is not { } intent)
            {
                return Result<PaymentView>.Failure(NotFound());
            }

            if (intent.Provider != ManualPaymentGateway.Name)
            {
                return Result<PaymentView>.Failure(Error.Conflict("payments.not_manual", "This payment is made through the provider, which reports it."));
            }

            if (!Open.Contains(intent.Status))
            {
                return Result<PaymentView>.Failure(Error.Conflict("payments.settled", "This payment is already settled."));
            }

            intent.Status = "succeeded";
            intent.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Received, org, intent.Id, note is { Length: > 0 and <= 200 } ? note.Trim() : null, ct).ConfigureAwait(false);
            return Result<PaymentView>.Success(View(intent));
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    private sealed record Place(Guid? ApplicationId, Guid? EnrollmentId, Guid ListingId, short? HouseholdSize);

    /// <summary>The family's accepted or offered application, or active enrolment, that a payment is for.</summary>
    private async Task<Place?> PlaceAsync(UserId owner, Guid? applicationId, Guid? enrollmentId, CancellationToken ct)
    {
        if (applicationId is { } a)
        {
            var found = await (from ap in db.Applications.AsNoTracking()
                               join p in db.FamilyProfiles on ap.FamilyProfileId equals p.Id
                               where ap.Id == a && p.OwnerUserId == owner.Value
                                     && (ap.Status == ApplicationStatuses.Accepted || ap.Status == ApplicationStatuses.Offered)
                               select new { ap.Id, ap.ListingId, p.HouseholdSize }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            Guid? enrolment = found is null ? null : await db.Enrollments.AsNoTracking()
                .Where(e => e.ApplicationId == found.Id && e.Status == EnrollmentStatuses.Active).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return found is null ? null : new Place(found.Id, enrolment, found.ListingId, found.HouseholdSize);
        }

        if (enrollmentId is { } e)
        {
            var found = await (from en in db.Enrollments.AsNoTracking()
                               join m in db.FamilyMembers on en.FamilyMemberId equals m.Id
                               join p in db.FamilyProfiles on m.FamilyProfileId equals p.Id
                               where en.Id == e && p.OwnerUserId == owner.Value && en.Status == EnrollmentStatuses.Active
                               select new { en.Id, en.ApplicationId, en.ListingId, p.HouseholdSize }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return found is null ? null : new Place(found.ApplicationId, found.Id, found.ListingId, found.HouseholdSize);
        }

        return null;
    }

    private async Task<PaymentIntentRecord?> OwnAsync(UserId owner, Guid id, CancellationToken ct) =>
        await (from p in db.PaymentIntents.AsNoTracking()
               join a in db.Applications on p.ApplicationId equals a.Id
               join f in db.FamilyProfiles on a.FamilyProfileId equals f.Id
               where p.Id == id && f.OwnerUserId == owner.Value
               select p).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<PaymentIntentRecord?> OrganisationsAsync(OrganisationId org, Guid id, CancellationToken ct)
    {
        var found = await (from p in db.PaymentIntents
                           join a in db.Applications on p.ApplicationId equals a.Id
                           where p.Id == id
                           select new { p, a.ListingId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return found is not null && (await listings.GetForReviewAsync(tenant.TenantId, found.ListingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org
            ? found.p
            : null;
    }

    /// <summary>A provider call, or null when the provider refused or couldn't be reached (logged; the family is told to try later).</summary>
    private async Task<PaymentIntentDescriptor?> ProviderAsync(Func<Task<PaymentIntentDescriptor>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (PaymentProviderException ex)
        {
            PaymentLog.ProviderFailed(logger, ex.Message, ex);
            return null;
        }
    }

    private static Error Unavailable() => new(ErrorKind.Unavailable, "payments.provider", "The payment provider can't be reached. Try again in a few minutes.");

    internal static string Status(PaymentIntentState state) => state switch
    {
        PaymentIntentState.Created => "created",
        PaymentIntentState.RequiresAction => "requires_action",
        PaymentIntentState.Processing => "processing",
        PaymentIntentState.Succeeded => "succeeded",
        PaymentIntentState.Failed => "failed",
        PaymentIntentState.Cancelled => "cancelled",
        PaymentIntentState.Refunded => "refunded",
        _ => "created",
    };

    private static PaymentView View(PaymentIntentRecord p) =>
        new(p.Id, p.ApplicationId, p.EnrollmentId, p.FeeScheduleId, p.AmountCents, p.Currency, p.Provider, p.Status, p.WaivedReason, p.CreatedAt);

    private Task AuditAsync(string action, OrganisationId org, Guid intent, string? reason, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "payment_intent",
            SubjectIds = [intent],
            ActorOrgId = org,
            Detail = reason is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason },
        }, ct);

    private static Error NotFound() => Error.NotFound("payments.not_found", "There's no such payment here.");
}
