using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Geo;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>A site in an organisation's list.</summary>
public sealed record SiteRow(Guid Id, string Name, string? AddressLine1, string? Locality, Guid? NeighbourhoodId, bool IsAddressPublic, bool IsActive);

/// <summary>
/// An organisation's sites, edited by its own staff. Every change is audited in its own
/// transaction (D19).
/// </summary>
/// <remarks>
/// A site is placed at its neighbourhood's centre with <c>locality</c> accuracy until a geocoder
/// exists (Q7). Deactivating keeps the row, so listings and history still name it. An edit or a
/// change of open state publishes <see cref="SiteChanged"/> in the same transaction, so the
/// listings at the site are re-projected with its new place, or without it.
/// </remarks>
public sealed class SiteEditor(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    public Task<IReadOnlyList<SiteRow>> ListAsync(Guid organisation, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SiteRow>>(async ct =>
            await db.Sites.AsNoTracking()
                .Where(s => s.OrgId == organisation)
                .OrderByDescending(s => s.IsActive).ThenBy(s => s.Name)
                .Select(s => new SiteRow(s.Id, s.Name, s.AddressLine1, s.Locality, s.NeighbourhoodId, s.IsAddressPublic, s.IsActive))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>The site as a form, or null when it is not this organisation's.</summary>
    public Task<SiteForm?> LoadAsync(Guid organisation, Guid site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Site? s = await db.Sites.AsNoTracking().FirstOrDefaultAsync(x => x.Id == site && x.OrgId == organisation, ct).ConfigureAwait(false);
            if (s is null)
            {
                return null;
            }

            List<SiteHours> hours = await db.SiteHours.AsNoTracking().Where(h => h.SiteId == site)
                .OrderBy(h => h.Season).ThenBy(h => h.DayOfWeek).ThenBy(h => h.OpensAt).ToListAsync(ct).ConfigureAwait(false);
            return new SiteForm
            {
                Name = s.Name,
                AddressLine1 = s.AddressLine1 ?? "",
                AddressLine2 = s.AddressLine2 ?? "",
                Locality = s.Locality ?? "",
                PostalCode = s.PostalCode ?? "",
                NeighbourhoodId = s.NeighbourhoodId?.ToString() ?? "",
                IsAddressPublic = s.IsAddressPublic,
                EntranceNote = s.EntranceNote ?? "",
                TransitNote = s.TransitNote ?? "",
                ParkingNote = s.ParkingNote ?? "",
                Wheelchair = ProviderForms.TriState(s.IsWheelchairAccessible),
                Restroom = ProviderForms.TriState(s.HasAccessibleRestroom),
                Elevator = ProviderForms.TriState(s.HasElevator),
                QuietSpace = ProviderForms.TriState(s.HasQuietSpace),
                Hours = [.. hours.Select(h => new SiteHoursForm(
                    h.DayOfWeek.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    h.OpensAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    h.ClosesAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    h.Season))],
                Active = s.IsActive,
            };
        }, cancellationToken);

    /// <summary>Creates or updates a site. The form must already have passed <see cref="ProviderForms.Check(SiteForm, IReadOnlyCollection{Guid})"/>.</summary>
    /// <returns>The site's id, or null when <paramref name="site"/> is not this organisation's.</returns>
    public Task<Guid?> SaveAsync(Guid organisation, Guid? site, SiteForm form, Coordinate location, UserId actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        return db.InTransactionAsync<Guid?>(async ct =>
        {
            Site entity;
            if (site is { } id)
            {
                Site? existing = await db.Sites.FirstOrDefaultAsync(s => s.Id == id && s.OrgId == organisation, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    return null;
                }

                entity = existing;
                Apply(entity, form, location);
                entity.UpdatedAt = clock.UtcNow;
                await db.SiteHours.Where(h => h.SiteId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                entity = NewSite(organisation, tenant.TenantId.Value, form, location);
                db.Sites.Add(entity);
            }

            foreach (SiteHours h in form.Hours.Where(h => !ProviderForms.IsBlank(h)).Select(ProviderForms.Hours).OfType<SiteHours>().DistinctBy(h => (h.DayOfWeek, h.Season, h.OpensAt)))
            {
                db.SiteHours.Add(new SiteHours { SiteId = entity.Id, DayOfWeek = h.DayOfWeek, OpensAt = h.OpensAt, ClosesAt = h.ClosesAt, Season = h.Season });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (site is not null)
            {
                await PublishChangedAsync(entity, ct).ConfigureAwait(false);
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = site is null ? "site.created" : "site.updated",
                ActorId = actor,
                ActorOrgId = new OrganisationId(organisation),
                SubjectKind = "site",
                SubjectIds = [entity.Id],
            }, ct).ConfigureAwait(false);
            return entity.Id;
        }, cancellationToken);
    }

    /// <summary>Closes or reopens a site. False when it is not this organisation's.</summary>
    public Task<bool> SetActiveAsync(Guid organisation, Guid site, bool active, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Site? s = await db.Sites.FirstOrDefaultAsync(x => x.Id == site && x.OrgId == organisation, ct).ConfigureAwait(false);
            if (s is null)
            {
                return false;
            }

            if (s.IsActive != active)
            {
                s.IsActive = active;
                s.UpdatedAt = clock.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await PublishChangedAsync(s, ct).ConfigureAwait(false);
                await audit.RecordInTransactionAsync(db, new AuditAccess
                {
                    Action = active ? "site.reactivated" : "site.deactivated",
                    ActorId = actor,
                    ActorOrgId = new OrganisationId(organisation),
                    SubjectKind = "site",
                    SubjectIds = [s.Id],
                }, ct).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken);

    private Task PublishChangedAsync(Site site, CancellationToken ct) =>
        db.PublishAsync(tenant.TenantId,
            new SiteChanged(new OrganisationId(site.OrgId), site.Id, site.NeighbourhoodId, site.IsActive, clock.UtcNow), ct);

    internal static Site NewSite(Guid organisation, Guid tenantId, SiteForm form, Coordinate location)
    {
        Site site = new() { Id = UuidV7.NewId(), TenantId = tenantId, OrgId = organisation, Name = form.Name.Trim() };
        Apply(site, form, location);
        return site;
    }

    private static void Apply(Site s, SiteForm f, Coordinate location)
    {
        s.Name = f.Name.Trim();
        s.AddressLine1 = ProviderForms.Blank(f.AddressLine1);
        s.AddressLine2 = ProviderForms.Blank(f.AddressLine2);
        s.Locality = ProviderForms.Blank(f.Locality);
        s.RegionCode = "NY";
        s.PostalCode = ProviderForms.Blank(f.PostalCode);
        s.NeighbourhoodId = Guid.Parse(f.NeighbourhoodId);
        s.IsAddressPublic = f.IsAddressPublic;
        s.EntranceNote = ProviderForms.Blank(f.EntranceNote);
        s.TransitNote = ProviderForms.Blank(f.TransitNote);
        s.ParkingNote = ProviderForms.Blank(f.ParkingNote);
        s.IsWheelchairAccessible = ProviderForms.TriState(f.Wheelchair);
        s.HasAccessibleRestroom = ProviderForms.TriState(f.Restroom);
        s.HasElevator = ProviderForms.TriState(f.Elevator);
        s.HasQuietSpace = ProviderForms.TriState(f.QuietSpace);
        s.Location = OrganisationApplications.PointAt(location);
        s.GeocodeAccuracy = GeocodeAccuracyValues.Locality;
    }
}
