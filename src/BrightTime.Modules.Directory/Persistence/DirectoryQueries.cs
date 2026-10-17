using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Persistence;

/// <summary>
/// What other modules ask Directory, as no-tracking projections. Every read runs inside a
/// transaction, which is where the tenant is set; outside one the context refuses.
/// </summary>
public sealed class DirectoryQueries(DirectoryDbContext db, IClock clock) : IDirectoryQueries
{
    public Task<IReadOnlyList<OrganisationMembership>> GetMembershipsAsync(
        UserId userId,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<OrganisationMembership>>(async ct =>
            await db.Memberships
                .AsNoTracking()
                .Where(m => m.UserId == userId.Value
                            && m.Status == MembershipStatus.Active
                            && m.EndedAt == null)
                .Select(m => new OrganisationMembership(
                    new OrganisationId(m.OrgId),
                    new UserId(m.UserId),
                    m.OrgRole,
                    m.JoinedAt!.Value))
                .ToListAsync(ct)
                .ConfigureAwait(false),
            cancellationToken);

    public Task<string?> GetOrganisationRoleAsync(
        OrganisationId organisationId,
        UserId userId,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct =>
            db.Memberships
                .AsNoTracking()
                .Where(m => m.OrgId == organisationId.Value
                            && m.UserId == userId.Value
                            && m.Status == MembershipStatus.Active
                            && m.EndedAt == null)
                .Select(m => m.OrgRole)
                .FirstOrDefaultAsync(ct),
            cancellationToken);

    public Task<IReadOnlyList<SiteSummary>> GetSitesAsync(
        IReadOnlyCollection<Guid> siteIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(siteIds);

        if (siteIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SiteSummary>>([]);
        }

        Guid[] ids = [.. siteIds.Distinct()];

        return db.InTransactionAsync<IReadOnlyList<SiteSummary>>(async ct =>
        {
            List<Site> sites = await db.Sites
                .AsNoTracking()
                .Where(s => ids.Contains(s.Id))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return [.. sites.Select(ToSummary)];
        }, cancellationToken);
    }

    public Task<string?> GetOrganisationNameAsync(
        OrganisationId organisationId,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct =>
            db.Organisations
                .AsNoTracking()
                .Where(o => o.Id == organisationId.Value)
                .Select(o => o.DisplayName)
                .FirstOrDefaultAsync(ct),
            cancellationToken);

    public Task<OrganisationStatus?> GetStatusAsync(
        OrganisationId organisationId,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? status = await db.Organisations
                .AsNoTracking()
                .Where(o => o.Id == organisationId.Value)
                .Select(o => o.Status)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return OrganisationStatusExtensions.TryFromDbValue(status, out OrganisationStatus resolved)
                ? (OrganisationStatus?)resolved
                : null;
        }, cancellationToken);

    public Task<IReadOnlyList<ClearanceState>> GetClearancesAsync(
        UserId userId,
        OrganisationId? organisationId = null,
        CancellationToken cancellationToken = default)
    {
        // Tenant-wide clearances (org_id IS NULL) travel with the person, so they count
        // wherever they work. One recorded against an organisation does not leak to another.
        Guid? orgFilter = organisationId?.Value;

        return db.InTransactionAsync<IReadOnlyList<ClearanceState>>(async ct =>
        {
            List<StaffClearance> rows = await db.Clearances
                .AsNoTracking()
                .Where(c => c.UserId == userId.Value
                            && (c.OrgId == null || c.OrgId == orgFilter))
                .ToListAsync(ct)
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
        }, cancellationToken);
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

    private static SiteSummary ToSummary(Site site)
    {
        bool showAddress = site.IsAddressPublic;

        return new SiteSummary(
            site.Id,
            new OrganisationId(site.OrgId),
            site.Name,
            site.NeighbourhoodId,
            site.Location is { } point ? new Coordinate(point.Y, point.X) : null,
            site.IsAddressPublic,
            showAddress ? site.AddressLine1 : null,
            showAddress ? site.AddressLine2 : null,
            showAddress ? site.Locality : null,
            showAddress ? site.PostalCode : null,
            // A transit note is directions: "bus 9 to Linden Lane" locates a private site as
            // well as its street line would, so it is withheld with the address.
            showAddress ? site.TransitNote : null,
            site.IsWheelchairAccessible,
            site.IsActive);
    }
}
