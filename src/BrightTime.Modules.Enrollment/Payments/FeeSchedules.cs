using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Payments;

public sealed record TierInput(string Label, int AmountCents, short? HouseholdSizeMin = null, short? HouseholdSizeMax = null, string? IncomeBand = null);

/// <summary>A fee to set. With an id, the existing fee of that id; without, a new one.</summary>
public sealed record FeeInput(Guid? Id, string Label, int AmountCents, string Cadence, bool IsDefault, IReadOnlyList<TierInput>? Tiers = null);

public sealed record TierView(Guid Id, string Label, int AmountCents, short? HouseholdSizeMin, short? HouseholdSizeMax, string? IncomeBand);

public sealed record FeeView(Guid Id, string Label, int AmountCents, string Currency, string Cadence, bool IsDefault, IReadOnlyList<TierView> Tiers);

/// <summary>What a family is asked to pay for an application: the listing's fees, with the tiers its household could choose.</summary>
public sealed record FeeQuote(Guid ApplicationId, short? HouseholdSize, IReadOnlyList<FeeView> Fees);

/// <summary>
/// What a listing charges (M3b, D55): fees, one of them the default, each with optional
/// sliding-scale tiers by household size and by an income band the family chooses.
/// </summary>
/// <remarks>
/// <para>
/// A family's income is never asked for or kept. A tier names a band ("under $35,000"); the family
/// picks the tier it fits when it pays, and only the amount is recorded. Household size, which
/// the family profile already holds, decides which tiers are offered.
/// </para>
/// <para>
/// A fee that a payment refers to can be changed but not removed (<c>fees.in_use</c>): the payment
/// keeps its own amount, and its record of what it paid for.
/// </para>
/// </remarks>
public sealed class FeeSchedules(EnrollmentDbContext db, ITenantContext tenant, IListingDetails listings)
{
    public const int MaxFees = 10;
    public const int MaxTiers = 10;
    public const int MaxAmountCents = 10_000_000;

    public static IReadOnlySet<string> Cadences { get; } = new HashSet<string>(["one_time", "weekly", "monthly", "per_session", "seasonal"], StringComparer.Ordinal);

    public Task<Result<IReadOnlyList<FeeView>>> GetAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false)
                ? Result<IReadOnlyList<FeeView>>.Success(await FeesAsync(listingId, ct).ConfigureAwait(false))
                : Result<IReadOnlyList<FeeView>>.Failure(NoListing()), cancellationToken);

    /// <summary>Sets a listing's fees: those given (by id, or new), and none other. A fee a payment refers to stays.</summary>
    public Task<Result<IReadOnlyList<FeeView>>> SetAsync(OrganisationId org, Guid listingId, IReadOnlyList<FeeInput> fees, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(fees);
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<IReadOnlyList<FeeView>>.Failure(NoListing());
            }

            if (Check(fees) is { } invalid)
            {
                return Result<IReadOnlyList<FeeView>>.Failure(invalid);
            }

            List<FeeScheduleRecord> existing = await db.FeeSchedules.Where(f => f.ListingId == listingId).ToListAsync(ct).ConfigureAwait(false);
            if (fees.Any(f => f.Id is { } id && existing.All(e => e.Id != id)))
            {
                return Result<IReadOnlyList<FeeView>>.Failure(Error.Validation("fees.unknown", "A fee id that isn't this listing's."));
            }

            HashSet<Guid> kept = [.. fees.Where(f => f.Id is not null).Select(f => f.Id!.Value)];
            List<Guid> dropped = [.. existing.Where(e => !kept.Contains(e.Id)).Select(e => e.Id)];
            Guid[] all = [.. existing.Select(e => e.Id)];
            if (await db.PaymentIntents.AnyAsync(p => p.FeeScheduleId != null && dropped.Contains(p.FeeScheduleId.Value), ct).ConfigureAwait(false))
            {
                return Result<IReadOnlyList<FeeView>>.Failure(Error.Conflict("fees.in_use", "A payment refers to a fee you left out. Keep it, and change it if you need to."));
            }

            // Tiers are rewritten whole; fees a payment may refer to are updated in place, with the
            // default cleared first so the one-default index never sees two at once.
            await db.SlidingScaleTiers.Where(t => all.Contains(t.FeeScheduleId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.FeeSchedules.Where(f => dropped.Contains(f.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.FeeSchedules.Where(f => kept.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.IsDefault, false), ct).ConfigureAwait(false);
            return Result<IReadOnlyList<FeeView>>.Success(await WriteAsync(listingId, fees, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>The fees a family's accepted application asks for, with the tiers its household size fits.</summary>
    public Task<Result<FeeQuote>> QuoteAsync(UserId owner, Guid applicationId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            var found = await (from a in db.Applications.AsNoTracking()
                               join p in db.FamilyProfiles on a.FamilyProfileId equals p.Id
                               where a.Id == applicationId && p.OwnerUserId == owner.Value
                               select new { a.ListingId, p.HouseholdSize }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                return Result<FeeQuote>.Failure(Error.NotFound("application.not_found", "You have no application with that id."));
            }

            List<FeeView> fees = [.. (await FeesAsync(found.ListingId, ct).ConfigureAwait(false))
                .Select(f => f with { Tiers = [.. f.Tiers.Where(t => Fits(t, found.HouseholdSize))] })];
            return Result<FeeQuote>.Success(new FeeQuote(applicationId, found.HouseholdSize, fees));
        }, cancellationToken);

    /// <summary>A tier offered to a household of this size: no size range, or one that includes it.</summary>
    public static bool Fits(TierView tier, short? householdSize)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return (tier.HouseholdSizeMin is null && tier.HouseholdSizeMax is null)
            || (householdSize is { } size && (tier.HouseholdSizeMin is null || size >= tier.HouseholdSizeMin) && (tier.HouseholdSizeMax is null || size <= tier.HouseholdSizeMax));
    }

    internal async Task<IReadOnlyList<FeeView>> FeesAsync(Guid listingId, CancellationToken ct)
    {
        List<FeeScheduleRecord> fees = await db.FeeSchedules.AsNoTracking().Where(f => f.ListingId == listingId)
            .OrderByDescending(f => f.IsDefault).ThenBy(f => f.Label).ToListAsync(ct).ConfigureAwait(false);
        Guid[] ids = [.. fees.Select(f => f.Id)];
        ILookup<Guid, SlidingScaleTierRecord> tiers = (await db.SlidingScaleTiers.AsNoTracking().Where(t => ids.Contains(t.FeeScheduleId))
            .OrderBy(t => t.SortOrder).ToListAsync(ct).ConfigureAwait(false)).ToLookup(t => t.FeeScheduleId);
        return [.. fees.Select(f => new FeeView(f.Id, f.Label, f.AmountCents, f.Currency, f.Cadence, f.IsDefault,
            [.. tiers[f.Id].Select(t => new TierView(t.Id, t.Label, t.AmountCents, t.HouseholdSizeMin, t.HouseholdSizeMax, t.IncomeBand))]))];
    }

    private async Task<IReadOnlyList<FeeView>> WriteAsync(Guid listingId, IReadOnlyList<FeeInput> fees, CancellationToken ct)
    {
        // Every kept fee is now not the default, and the input names one: at most one row is ever the default.
        foreach (FeeInput fee in fees.OrderBy(f => f.IsDefault))
        {
            FeeScheduleRecord record;
            if (fee.Id is { } id)
            {
                record = await db.FeeSchedules.FirstAsync(f => f.Id == id, ct).ConfigureAwait(false);
                record.Label = fee.Label.Trim();
                record.AmountCents = fee.AmountCents;
                record.Cadence = fee.Cadence;
                record.IsDefault = fee.IsDefault;
            }
            else
            {
                record = new()
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    ListingId = listingId,
                    Label = fee.Label.Trim(),
                    AmountCents = fee.AmountCents,
                    Currency = "USD",
                    Cadence = fee.Cadence,
                    IsDefault = fee.IsDefault,
                };
                db.FeeSchedules.Add(record);
            }

            short order = 0;
            foreach (TierInput tier in fee.Tiers ?? [])
            {
                db.SlidingScaleTiers.Add(new SlidingScaleTierRecord
                {
                    Id = UuidV7.NewId(),
                    FeeScheduleId = record.Id,
                    Label = tier.Label.Trim(),
                    AmountCents = tier.AmountCents,
                    HouseholdSizeMin = tier.HouseholdSizeMin,
                    HouseholdSizeMax = tier.HouseholdSizeMax,
                    IncomeBand = string.IsNullOrWhiteSpace(tier.IncomeBand) ? null : tier.IncomeBand.Trim(),
                    SortOrder = order++,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await FeesAsync(listingId, ct).ConfigureAwait(false);
    }

    private static Error? Check(IReadOnlyList<FeeInput> fees)
    {
        if (fees.Count > MaxFees || (fees.Count > 0 && fees.Count(f => f.IsDefault) != 1))
        {
            return Error.Validation("fees.count", $"Up to {MaxFees} fees, exactly one of them the default.");
        }

        foreach (FeeInput fee in fees)
        {
            if (fee is null || fee.Label?.Trim() is not { Length: > 0 and <= 80 } || fee.AmountCents is < 0 or > MaxAmountCents || !Cadences.Contains(fee.Cadence ?? ""))
            {
                return Error.Validation("fees.fee", "A label of up to 80 characters, an amount from $0 to $100,000, and how often it is paid.");
            }

            IReadOnlyList<TierInput> tiers = fee.Tiers ?? [];
            if (tiers.Count > MaxTiers || tiers.Any(t => t is null || t.Label?.Trim() is not { Length: > 0 and <= 80 } || t.AmountCents < 0 || t.AmountCents > fee.AmountCents
                    || t.HouseholdSizeMin is < 1 or > 30 || t.HouseholdSizeMax is < 1 or > 30 || (t.HouseholdSizeMin > t.HouseholdSizeMax) || t.IncomeBand is { Length: > 60 }))
            {
                return Error.Validation("fees.tier", "Up to 10 tiers per fee, each no more than the fee, with a household size from 1 to 30.");
            }
        }

        return fees.Where(f => f.Id is not null).GroupBy(f => f.Id).Any(g => g.Count() > 1)
            ? Error.Validation("fees.unknown", "Each fee once.")
            : null;
    }

    private async Task<bool> OwnsListingAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org;

    private static Error NoListing() => Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id.");
}
