using BrightTime.Infrastructure.Outbox;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Contracts.Messages;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Directory.Review;

internal static class OrganisationMessages
{
    /// <summary>
    /// Publishes a status change in the caller's transaction: always <see cref="OrganisationStatusChanged"/>,
    /// and <see cref="OrganisationApproved"/> or <see cref="OrganisationSuspended"/> when the move is one.
    /// </summary>
    public static async Task PublishStatusChangedAsync(
        this DirectoryDbContext db, TenantId tenant, Organisation o, OrganisationStatus from, OrganisationStatus to,
        UserId actor, DateTimeOffset at, CancellationToken ct)
    {
        OrganisationId id = new(o.Id);
        await db.PublishAsync(tenant, new OrganisationStatusChanged(id, from, to, at), ct).ConfigureAwait(false);
        if (to == OrganisationStatus.Approved)
        {
            await db.PublishAsync(tenant, new OrganisationApproved(id, o.DisplayName, actor, at), ct).ConfigureAwait(false);
        }
        else if (to == OrganisationStatus.Suspended)
        {
            await db.PublishAsync(tenant, new OrganisationSuspended(id, o.SuspensionReason ?? string.Empty, actor, at), ct).ConfigureAwait(false);
        }
    }
}
