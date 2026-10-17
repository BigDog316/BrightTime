using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Persistence;

/// <summary>
/// Approved organisations' public pages. An organisation that is applying, suspended or
/// rejected has no page, and its slug answers exactly as one that never existed.
/// </summary>
public sealed class OrganisationProfiles(DirectoryDbContext db) : IOrganisationProfiles
{
    private static readonly string Approved = OrganisationStatus.Approved.ToDbValue();

    public Task<OrganisationProfile?> GetAsync(string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Organisation? o = await db.Organisations.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Slug == slug && x.Status == Approved, ct).ConfigureAwait(false);
            if (o is null)
            {
                return null;
            }

            List<Site> sites = await db.Sites.AsNoTracking()
                .Where(s => s.OrgId == o.Id && s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync(ct).ConfigureAwait(false);

            return new OrganisationProfile(
                new OrganisationId(o.Id), o.Slug, o.DisplayName, o.OrgKind, o.Mission, o.WebsiteUrl, o.PublicEmail, o.PublicPhone,
                o.LanguagesServed, [.. sites.Select(ToPublic)]);
        }, cancellationToken);

    public Task<IReadOnlyDictionary<OrganisationId, OrganisationLink>> GetLinksAsync(
        IReadOnlyCollection<OrganisationId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Guid[] keys = [.. ids.Select(i => i.Value).Distinct()];
        return keys.Length == 0
            ? Task.FromResult<IReadOnlyDictionary<OrganisationId, OrganisationLink>>(new Dictionary<OrganisationId, OrganisationLink>())
            : db.InTransactionAsync<IReadOnlyDictionary<OrganisationId, OrganisationLink>>(async ct =>
                (await db.Organisations.AsNoTracking()
                    .Where(o => keys.Contains(o.Id) && o.Status == Approved)
                    .Select(o => new { o.Id, o.Slug, o.DisplayName })
                    .ToListAsync(ct).ConfigureAwait(false))
                .ToDictionary(o => new OrganisationId(o.Id), o => new OrganisationLink(new OrganisationId(o.Id), o.Slug, o.DisplayName)),
                cancellationToken);
    }

    private static PublicSite ToPublic(Site s)
    {
        bool open = s.IsAddressPublic;
        return new PublicSite(
            s.Name,
            s.NeighbourhoodId,
            open,
            open ? s.AddressLine1 : null,
            open ? s.AddressLine2 : null,
            open ? s.Locality : null,
            open ? s.PostalCode : null,
            open ? s.TransitNote : null,
            s.IsWheelchairAccessible,
            s.HasAccessibleRestroom,
            s.HasElevator,
            s.HasQuietSpace);
    }
}
