using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Clearances;

/// <summary>One clearance on file: kind, status and dates, and whether it counts county-wide. Never a report.</summary>
public sealed record ClearanceEntry(string Kind, string Status, DateOnly? CompletedOn, DateOnly? ExpiresOn, bool CountyWide, DateTimeOffset RecordedAt);

/// <summary>
/// Whether someone holds what working with young people needs (D98): the required kinds missing
/// or expired, and those that expire within the warning window.
/// </summary>
public sealed record ClearanceReadiness(bool Ready, IReadOnlyList<string> Missing, IReadOnlyList<string> Expired, IReadOnlyList<(string Kind, DateOnly ExpiresOn)> ExpiringSoon);

/// <summary>A staff member's clearances at one organisation: theirs there, and county-wide ones.</summary>
public sealed record MemberClearances(Guid MembershipId, Guid UserId, string Role, string Status, string? Title, IReadOnlyList<ClearanceEntry> Held, ClearanceReadiness Readiness);

/// <summary>What an admin or organiser records. Dates count only for a clearance that passed.</summary>
public sealed record ClearanceInput(string Kind, string Status, DateOnly? CompletedOn, DateOnly? ExpiresOn, bool CountyWide);

/// <summary>
/// Staff clearances in the console (D98, docs/plan/04 <c>clearances.record</c>): status and dates
/// only, as the schema allows. An organisation's owners and admins record them for their own staff,
/// counted at that organisation; the site's organisers may also record one for the whole county,
/// which counts wherever the person works. Every change is audited.
/// </summary>
/// <remarks>
/// Nothing here refuses anyone access yet: readiness is shown, and the expiry sweep warns and marks
/// lapses. Enforcing it on youth records is a later step behind a county setting.
/// </remarks>
public sealed class StaffClearances(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public const string Recorded = "directory.clearance.recorded";

    /// <summary>The statuses a person records. <c>expired</c> is the sweep's to set, when the date passes.</summary>
    public static readonly string[] Recordable =
    [
        ClearanceStatus.NotStarted.ToDbValue(), ClearanceStatus.Pending.ToDbValue(), ClearanceStatus.Cleared.ToDbValue(),
        ClearanceStatus.Conditional.ToDbValue(), ClearanceStatus.NotCleared.ToDbValue(), ClearanceStatus.Withdrawn.ToDbValue(),
    ];

    /// <summary>Every kind, in the order the console lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [.. Enum.GetValues<ClearanceKind>().Select(k => k.ToDbValue())];

    /// <summary>What working with young people needs: the platform's minimum.</summary>
    public static IReadOnlyList<string> Required { get; } = [.. ClearanceExtensions.YouthFacingMinimum.Select(k => k.ToDbValue())];

    private static readonly string[] Live = [MembershipStatus.Invited, MembershipStatus.Active, MembershipStatus.Suspended];

    /// <summary>Today in the county's zone: expiry dates are local days.</summary>
    public DateOnly Today => clock.TodayIn(tenant.TimeZone);

    /// <summary>
    /// Readiness from what's on file, judged on <paramref name="today"/> against
    /// <paramref name="required"/>: the county's kinds (D99), or the platform's minimum.
    /// </summary>
    public static ClearanceReadiness Assess(IEnumerable<(string Kind, string Status, DateOnly? ExpiresOn)> held, DateOnly today, IEnumerable<string>? required = null)
    {
        ArgumentNullException.ThrowIfNull(held);
        List<ClearanceState> states = [];
        foreach ((string kind, string status, DateOnly? expires) in held)
        {
            if (ClearanceExtensions.TryKindFromDbValue(kind, out ClearanceKind k) && ClearanceExtensions.TryStatusFromDbValue(status, out ClearanceStatus s))
            {
                states.Add(new ClearanceState(k, s, expires));
            }
        }

        List<ClearanceKind> kinds = required is null
            ? [.. ClearanceExtensions.YouthFacingMinimum]
            : [.. required.Select(r => ClearanceExtensions.TryKindFromDbValue(r, out ClearanceKind k) ? (ClearanceKind?)k : null).OfType<ClearanceKind>().Distinct()];
        ClearanceCurrency.Assessment a = ClearanceCurrency.Assess(states, kinds, today);
        return new ClearanceReadiness(a.IsCleared, [.. a.Missing.Select(k => k.ToDbValue())], [.. a.Expired.Select(k => k.ToDbValue())],
            [.. a.ExpiringSoon.Select(e => (e.Kind.ToDbValue(), e.ExpiresOn))]);
    }

    /// <summary>A live member's clearances at the organisation, judged against <paramref name="required"/> (the county's, D99), or null for anyone else.</summary>
    public Task<MemberClearances?> GetAsync(Guid organisation, Guid membershipId, IReadOnlyList<string>? required = null, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == membershipId && x.OrgId == organisation && Live.Contains(x.Status), ct).ConfigureAwait(false);
            if (m is null)
            {
                return null;
            }

            List<StaffClearance> rows = await db.Clearances.AsNoTracking()
                .Where(c => c.UserId == m.UserId && (c.OrgId == null || c.OrgId == organisation))
                .ToListAsync(ct).ConfigureAwait(false);
            List<ClearanceEntry> held = [.. rows
                .OrderBy(c => Kinds.ToList().IndexOf(c.ClearanceKind)).ThenBy(c => c.OrgId is null)
                .Select(c => new ClearanceEntry(c.ClearanceKind, c.Status, c.CompletedOn, c.ExpiresOn, c.OrgId is null, c.RecordedAt))];
            return new MemberClearances(m.Id, m.UserId, m.OrgRole, m.Status, m.Title, held,
                Assess(rows.Select(c => (c.ClearanceKind, c.Status, c.ExpiresOn)), Today, required));
        }, cancellationToken);

    /// <summary>
    /// Records a clearance for a live member: the organisation's own, or, for an organiser, the
    /// county's. A second record of the same kind and reach replaces the first.
    /// </summary>
    public Task<Result> RecordAsync(Guid organisation, Guid membershipId, ClearanceInput input, UserId actor, bool asOrganiser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (Check(input, asOrganiser, Today) is { } invalid)
        {
            return Task.FromResult(Result.Failure(invalid));
        }

        bool passed = input.Status is "cleared" or "conditional";
        return db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await db.Memberships.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == membershipId && x.OrgId == organisation && Live.Contains(x.Status), ct).ConfigureAwait(false);
            if (m is null)
            {
                return Result.Failure(Error.NotFound("clearances.member_not_found", "That person doesn't work for this organisation."));
            }

            // Once a county enforces clearances (D99), recording one's own would open one's own
            // access: someone else records it, an organiser for an owner.
            if (m.UserId == actor.Value && !asOrganiser)
            {
                return Result.Failure(Error.Forbidden("clearances.own", "Someone else records your own clearances: another owner or admin, or the site's organisers."));
            }

            Guid? scope = input.CountyWide ? null : organisation;
            StaffClearance? row = await db.Clearances
                .FirstOrDefaultAsync(c => c.UserId == m.UserId && c.ClearanceKind == input.Kind && c.OrgId == scope, ct).ConfigureAwait(false);
            DateOnly? completed = passed ? input.CompletedOn : null;
            DateOnly? expires = passed ? input.ExpiresOn : null;
            DateTimeOffset now = clock.UtcNow;
            if (row is null)
            {
                db.Clearances.Add(new StaffClearance
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    UserId = m.UserId,
                    OrgId = scope,
                    ClearanceKind = input.Kind,
                    Status = input.Status,
                    CompletedOn = completed,
                    ExpiresOn = expires,
                    RecordedBy = actor.Value,
                    RecordedAt = now,
                });
            }
            else
            {
                if (row.ExpiresOn != expires)
                {
                    // A new expiry is a new thing to warn about.
                    row.ExpiryNoticeFor = null;
                }

                row.Status = input.Status;
                row.CompletedOn = completed;
                row.ExpiresOn = expires;
                row.RecordedBy = actor.Value;
                row.RecordedAt = now;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Recorded,
                ActorId = actor,
                ActorOrgId = asOrganiser ? null : new OrganisationId(organisation),
                SubjectKind = "user",
                SubjectIds = [m.UserId],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = input.Kind,
                    ["status"] = input.Status,
                    ["reach"] = input.CountyWide ? "county" : "organisation",
                },
            }, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
    }

    /// <summary>What's wrong with a record, or null.</summary>
    public static Error? Check(ClearanceInput input, bool asOrganiser, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Kinds.Contains(input.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("clearances.kind", "Choose a kind of clearance.");
        }

        if (!Recordable.Contains(input.Status, StringComparer.Ordinal))
        {
            return Error.Validation("clearances.status", "Choose a status.");
        }

        if (input.CountyWide && !asOrganiser)
        {
            return Error.Forbidden("clearances.county_wide", "Only the site's organisers record a clearance for the whole county.");
        }

        if (input.Status is not ("cleared" or "conditional"))
        {
            return null;
        }

        if (input.CompletedOn is not { } completed)
        {
            return Error.Validation("clearances.completed_needed", "Give the date it was completed.");
        }

        if (completed > today)
        {
            return Error.Validation("clearances.completed_future", "The date it was completed can't be in the future.");
        }

        if (input.ExpiresOn is { } expires)
        {
            if (expires <= completed)
            {
                return Error.Validation("clearances.expiry_order", "It must expire after the date it was completed.");
            }

            if (expires < today)
            {
                return Error.Validation("clearances.expiry_past", "That expiry date has passed, so it isn't current.");
            }
        }

        return null;
    }
}
