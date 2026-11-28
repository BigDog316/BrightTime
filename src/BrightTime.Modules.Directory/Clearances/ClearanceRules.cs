using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Clearances;

/// <summary>Whether the county enforces clearances, and the kinds it requires.</summary>
public sealed record ClearanceRuleSet(bool Enforced, IReadOnlyList<string> Required);

/// <summary>
/// The county's clearance rules (D99) and the gate they make. Off by default: until an organiser
/// turns them on, nothing is refused. On, anyone at an organisation, owners included, needs a
/// current clearance of every required kind before acting on a named young person's record there:
/// the platform's minimum (background check, mandated reporter training) and any kinds the county
/// adds. A county-wide record counts like the organisation's own.
/// </summary>
/// <remarks>
/// One per request: the rules and each person's answer are read once, however many permission
/// checks a page makes.
/// </remarks>
public sealed class ClearanceRules(IFeatureFlags flags, DirectoryDbContext db, ITenantContext tenant, IClock clock) : IClearanceGate
{
    private ClearanceRuleSet? _rules;
    private readonly Dictionary<(Guid, Guid), ClearanceRefusal?> _answers = [];

    public async Task<ClearanceRuleSet> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_rules is null)
        {
            IReadOnlySet<string> on = await flags.EnabledAsync("clearances.", cancellationToken).ConfigureAwait(false);
            List<string> required = [.. StaffClearances.Required];
            required.AddRange(StaffClearances.Kinds.Where(k => !required.Contains(k) && on.Contains(ClearanceRuleKeys.Extra(k))));
            _rules = new ClearanceRuleSet(on.Contains(ClearanceRuleKeys.Enforced), [.. StaffClearances.Kinds.Where(required.Contains)]);
        }

        return _rules;
    }

    public async Task<ClearanceRefusal?> CheckAsync(UserId person, OrganisationId organisation, CancellationToken cancellationToken = default)
    {
        ClearanceRuleSet rules = await GetAsync(cancellationToken).ConfigureAwait(false);
        if (!rules.Enforced)
        {
            return null;
        }

        if (_answers.TryGetValue((person.Value, organisation.Value), out ClearanceRefusal? known))
        {
            return known;
        }

        Guid user = person.Value;
        Guid org = organisation.Value;
        var held = await db.InTransactionAsync(ct => db.Clearances.AsNoTracking()
            .Where(c => c.UserId == user && (c.OrgId == null || c.OrgId == org))
            .Select(c => new { c.ClearanceKind, c.Status, c.ExpiresOn })
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
        ClearanceReadiness readiness = StaffClearances.Assess(held.Select(c => (c.ClearanceKind, c.Status, c.ExpiresOn)), clock.TodayIn(tenant.TimeZone), rules.Required);
        ClearanceRefusal? answer = readiness.Ready ? null : new ClearanceRefusal(readiness.Missing, readiness.Expired);
        _answers[(user, org)] = answer;
        return answer;
    }

    public async Task<IReadOnlySet<Guid>> ClearedAsync(IReadOnlyCollection<(Guid Person, Guid Organisation)> people, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (people.Count == 0)
        {
            return new HashSet<Guid>();
        }

        ClearanceRuleSet rules = await GetAsync(cancellationToken).ConfigureAwait(false);
        Guid[] users = [.. people.Select(p => p.Person).Distinct()];
        var held = await db.InTransactionAsync(ct => db.Clearances.AsNoTracking()
            .Where(c => users.Contains(c.UserId))
            .Select(c => new { c.UserId, c.OrgId, c.ClearanceKind, c.Status, c.ExpiresOn })
            .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
        DateOnly today = clock.TodayIn(tenant.TimeZone);
        return people
            .Where(p => StaffClearances.Assess(
                held.Where(c => c.UserId == p.Person && (c.OrgId == null || c.OrgId == p.Organisation)).Select(c => (c.ClearanceKind, c.Status, c.ExpiresOn)),
                today, rules.Required).Ready)
            .Select(p => p.Person)
            .ToHashSet();
    }

    /// <summary>
    /// How many people working at approved organisations would be refused under
    /// <paramref name="required"/>: what an organiser sees before turning the rules on.
    /// </summary>
    public Task<int> UnclearedAsync(IReadOnlyList<string> required, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(required);
        string approved = OrganisationStatus.Approved.ToDbValue();
        return db.InTransactionAsync(async ct =>
        {
            var members = await (from m in db.Memberships.AsNoTracking()
                                 join o in db.Organisations.AsNoTracking() on m.OrgId equals o.Id
                                 where m.Status == MembershipStatus.Active && o.Status == approved
                                 select new { m.UserId, m.OrgId }).ToListAsync(ct).ConfigureAwait(false);
            Guid[] users = [.. members.Select(m => m.UserId).Distinct()];
            var clearances = await db.Clearances.AsNoTracking()
                .Where(c => users.Contains(c.UserId))
                .Select(c => new { c.UserId, c.OrgId, c.ClearanceKind, c.Status, c.ExpiresOn })
                .ToListAsync(ct).ConfigureAwait(false);
            DateOnly today = clock.TodayIn(tenant.TimeZone);
            return members
                .Where(m => !StaffClearances.Assess(
                    clearances.Where(c => c.UserId == m.UserId && (c.OrgId == null || c.OrgId == m.OrgId)).Select(c => (c.ClearanceKind, c.Status, c.ExpiresOn)),
                    today, required).Ready)
                .Select(m => m.UserId).Distinct().Count();
        }, cancellationToken);
    }
}
