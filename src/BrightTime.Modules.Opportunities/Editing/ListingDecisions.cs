using BrightTime.Infrastructure.Jobs;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Persistence;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Opportunities.Editing;

/// <summary>
/// Telling an organisation that a network organiser decided on one of its listings (D102): who is
/// told, the email jobs booked with the decision, and what the email reads when it's sent.
/// </summary>
public sealed class ListingDecisions(OpportunitiesDbContext db) : IListingDecisions
{
    /// <summary>
    /// Whoever submitted the listing or saved the edit, while they're still an active member, and
    /// every active owner and admin; each once, in the members' order.
    /// </summary>
    public static IReadOnlyList<Guid> Recipients(IEnumerable<OrganisationMembership> members, Guid? author)
    {
        ArgumentNullException.ThrowIfNull(members);
        List<Guid> told = [];
        foreach (OrganisationMembership m in members)
        {
            // ck_org_membership_role: the roles that look after the organisation.
            bool looksAfter = m.OrgRole is "owner" or "admin";
            if ((looksAfter || m.UserId.Value == author) && !told.Contains(m.UserId.Value))
            {
                told.Add(m.UserId.Value);
            }
        }

        return told;
    }

    /// <summary>
    /// Books one email per person told, in the decision's own transaction, so they go only if the
    /// decision is kept.
    /// </summary>
    internal static async Task BookAsync(
        OpportunitiesDbContext db, IDirectoryQueries directory, ITenantContext tenant,
        Guid orgId, Guid listingId, string decision, Guid? author, DateTimeOffset at, CancellationToken ct)
    {
        IReadOnlyList<OrganisationMembership> members = await directory.GetOrganisationMembersAsync(new OrganisationId(orgId), ct).ConfigureAwait(false);
        foreach (Guid recipient in Recipients(members, author))
        {
            ListingDecisionNotice notice = new(recipient, orgId, listingId, decision, at);
            await db.EnqueueJobAsync(new JobRequest
            {
                JobType = ListingDecisionNotice.JobType,
                TenantId = tenant.TenantId,
                Payload = notice,
                UniqueKey = notice.UniqueKey,
            }, ct).ConfigureAwait(false);
        }
    }

    public Task<ListingDecisionFacts?> GetAsync(Guid listingId, string decision, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<ListingDecisionFacts?>(async ct =>
        {
            var listing = await db.Listings.AsNoTracking()
                .Where(l => l.Id == listingId)
                .Select(l => new { l.Title, l.Status, l.ReviewNote })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (listing is null)
            {
                return null;
            }

            // The note only while the decision stands: a listing resubmitted, or an edit saved
            // again, carries a newer note or none, and that isn't what this email is about.
            string? note = decision switch
            {
                ListingDecisionNotice.ChangesRequested or ListingDecisionNotice.Rejected or ListingDecisionNotice.Unpublished
                    when listing.Status == decision => listing.ReviewNote,
                ListingDecisionNotice.EditReturned => await db.ListingPendingEdits.AsNoTracking()
                    .Where(e => e.ListingId == listingId && e.State == ListingPendingEditRecord.Returned)
                    .Select(e => e.ReviewNote)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false),
                _ => null,
            };
            return new ListingDecisionFacts(listing.Title, note);
        }, cancellationToken);
}
