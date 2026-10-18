using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.Modules.Platform.Contracts.Messages;
using BrightTime.SharedKernel.Messaging;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Projection;

/// <summary>
/// The other modules' changes that alter what search shows: a site moved or closed, an
/// organisation approved or suspended, a term renamed or retired. Each queues a projection of
/// every listing it touches. Delivered by the outbox dispatcher, at least once: queueing a
/// projection twice collapses into one, so a repeat is harmless.
/// </summary>
/// <remarks>Each projection is its own debounced job, so one slow listing does not hold up the rest.</remarks>
public abstract class ReprojectionHandler(OpportunitiesDbContext db, ITenantContext tenant)
{
    protected Task<int> ReprojectAsync(Func<OpportunitiesDbContext, IQueryable<Guid>> listings, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            List<Guid> ids = await listings(db).Distinct().ToListAsync(ct).ConfigureAwait(false);
            foreach (Guid id in ids)
            {
                await db.ScheduleProjectionAsync(tenant.TenantId, id, ct).ConfigureAwait(false);
            }

            return ids.Count;
        }, cancellationToken);
}

/// <summary>A site moved, was renamed into another neighbourhood, or closed: its listings are shown in the right place, or no longer there.</summary>
public sealed class SiteChangedHandler(OpportunitiesDbContext db, ITenantContext tenant)
    : ReprojectionHandler(db, tenant), IModuleMessageHandler<SiteChanged>
{
    public Task HandleAsync(SiteChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ReprojectAsync(c => c.ListingSites.AsNoTracking().Where(x => x.SiteId == message.SiteId).Select(x => x.ListingId), cancellationToken);
    }
}

/// <summary>An organisation's status changed: its listings are findable only while it is approved.</summary>
public sealed class OrganisationStatusChangedHandler(OpportunitiesDbContext db, ITenantContext tenant)
    : ReprojectionHandler(db, tenant), IModuleMessageHandler<OrganisationStatusChanged>
{
    public Task HandleAsync(OrganisationStatusChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        Guid org = message.OrganisationId.Value;
        return ReprojectAsync(c => c.Listings.AsNoTracking().Where(l => l.OrgId == org).Select(l => l.Id), cancellationToken);
    }
}

/// <summary>A term was renamed, retired or restored: listings tagged with it match its current name.</summary>
public sealed class TaxonomyTermChangedHandler(OpportunitiesDbContext db, ITenantContext tenant)
    : ReprojectionHandler(db, tenant), IModuleMessageHandler<TaxonomyTermChanged>
{
    public Task HandleAsync(TaxonomyTermChanged message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        return ReprojectAsync(c => c.ListingTerms.AsNoTracking().Where(x => x.TermId == message.TermId).Select(x => x.ListingId)
            .Union(c.ListingAccommodations.AsNoTracking().Where(x => x.TermId == message.TermId).Select(x => x.ListingId)), cancellationToken);
    }
}
