using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Persistence;

/// <summary>What other modules ask Directory, implemented as no-tracking projections.</summary>
public sealed class DirectoryQueries(DirectoryDbContext db, IClock clock) : IDirectoryQueries
{
    public async Task<IReadOnlyList<OrganisationMembership>> GetMembershipsAsync(
        UserId userId,
        CancellationToken cancellationToken = default)
    {
        List<OrganisationMembership> memberships = await db.Memberships
            .AsNoTracking()
            .Where(m => m.UserId == userId.Value
                        && m.Status == MembershipStatus.Active
                        && m.EndedAt == null)
            .Select(m => new OrganisationMembership(
                new OrganisationId(m.OrgId),
                new UserId(m.UserId),
                m.OrgRole,
                m.JoinedAt!.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return memberships;
    }

    public Task<string?> GetOrganisationRoleAsync(
        OrganisationId organisationId,
        UserId userId,
        CancellationToken cancellationToken = default) =>
        db.Memberships
            .AsNoTracking()
            .Where(m => m.OrgId == organisationId.Value
                        && m.UserId == userId.Value
                        && m.Status == MembershipStatus.Active
                        && m.EndedAt == null)
            .Select(m => m.OrgRole)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<OrganisationStatus?> GetStatusAsync(
        OrganisationId organisationId,
        CancellationToken cancellationToken = default)
    {
        string? status = await db.Organisations
            .AsNoTracking()
            .Where(o => o.Id == organisationId.Value)
            .Select(o => o.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return OrganisationStatusExtensions.TryFromDbValue(status, out OrganisationStatus resolved)
            ? resolved
            : null;
    }

    public async Task<IReadOnlyList<ClearanceState>> GetClearancesAsync(
        UserId userId,
        OrganisationId? organisationId = null,
        CancellationToken cancellationToken = default)
    {
        // Tenant-wide clearances (org_id IS NULL) travel with the person, so
        // they count wherever they work. A clearance recorded against one
        // organisation does not leak to another.
        Guid? orgFilter = organisationId?.Value;

        List<StaffClearance> rows = await db.Clearances
            .AsNoTracking()
            .Where(c => c.UserId == userId.Value
                        && (c.OrgId == null || c.OrgId == orgFilter))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        List<ClearanceState> states = [];
        foreach (StaffClearance row in rows)
        {
            if (ClearanceExtensions.TryKindFromDbValue(row.ClearanceKind, out ClearanceKind kind)
                && ClearanceExtensions.TryStatusFromDbValue(row.Status, out ClearanceStatus status))
            {
                states.Add(new ClearanceState(kind, status, row.ExpiresOn));
            }
        }

        return states;
    }

    public async Task<bool> HasCurrentClearancesAsync(
        UserId userId,
        OrganisationId organisationId,
        IReadOnlyList<ClearanceKind> required,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(required);

        if (required.Count == 0)
        {
            return true;
        }

        IReadOnlyList<ClearanceState> held = await GetClearancesAsync(userId, organisationId, cancellationToken)
            .ConfigureAwait(false);

        DateOnly today = clock.TodayIn(TimeZoneInfo.Utc);

        return Clearances.ClearanceCurrency
            .Assess(held, required, today)
            .IsCleared;
    }
}
