using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Domain;
using BrightTime.Modules.Directory.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Directory.Provider;

/// <summary>A clearance as the console may show it: kind, status, expiry. Never a report (see the schema).</summary>
public sealed record ClearanceView(string Kind, string Status, DateOnly? ExpiresOn);

/// <summary>A person's membership, for the staff page. The address comes from Identity, by user id.</summary>
public sealed record MemberRow(
    Guid MembershipId,
    Guid UserId,
    string Role,
    string Status,
    string? Title,
    DateTimeOffset InvitedAt,
    DateTimeOffset? JoinedAt,
    IReadOnlyList<Guid> SiteIds,
    IReadOnlyList<ClearanceView> Clearances);

/// <summary>An invitation waiting for the signed-in person to answer.</summary>
public sealed record Invitation(Guid MembershipId, OrganisationId OrganisationId, string OrganisationName, string Role, DateTimeOffset InvitedAt);

/// <summary>
/// Who works for an organisation, and in what role. Owners and admins invite and manage; only
/// an owner makes or unmakes another owner; the last owner can be neither demoted nor removed.
/// Every change is audited (D19).
/// </summary>
/// <remarks>
/// An invitation is a membership in the <c>invited</c> state. The invited person accepts it
/// after signing in, which proves they hold the address; until then it grants nothing, because
/// only active memberships reach the sign-in claims.
/// </remarks>
public sealed class StaffDirectory(DirectoryDbContext db, ITenantContext tenant, IClock clock, IAuditWriter audit)
{
    private static readonly string[] Live = [MembershipStatus.Invited, MembershipStatus.Active, MembershipStatus.Suspended];

    public Task<IReadOnlyList<MemberRow>> ListAsync(Guid organisation, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<MemberRow>>(async ct =>
        {
            List<OrgMembership> members = await db.Memberships.AsNoTracking()
                .Where(m => m.OrgId == organisation && Live.Contains(m.Status))
                .OrderByDescending(m => m.Status == MembershipStatus.Active).ThenBy(m => m.InvitedAt)
                .ToListAsync(ct).ConfigureAwait(false);
            Guid[] ids = [.. members.Select(m => m.Id)];
            Guid[] users = [.. members.Select(m => m.UserId)];
            List<SiteAssignment> sites = await db.SiteAssignments.AsNoTracking().Where(a => ids.Contains(a.OrgMembershipId)).ToListAsync(ct).ConfigureAwait(false);
            List<StaffClearance> clearances = await db.Clearances.AsNoTracking()
                .Where(c => users.Contains(c.UserId) && (c.OrgId == null || c.OrgId == organisation))
                .ToListAsync(ct).ConfigureAwait(false);

            return [.. members.Select(m => new MemberRow(
                m.Id, m.UserId, m.OrgRole, m.Status, m.Title, m.InvitedAt, m.JoinedAt,
                [.. sites.Where(a => a.OrgMembershipId == m.Id).Select(a => a.SiteId)],
                [.. clearances.Where(c => c.UserId == m.UserId).OrderBy(c => c.ClearanceKind).Select(c => new ClearanceView(c.ClearanceKind, c.Status, c.ExpiresOn))]))];
        }, cancellationToken);

    /// <summary>Invites <paramref name="invitee"/>. Refused if they already belong or are already invited.</summary>
    public Task<Result> InviteAsync(Guid organisation, UserId invitee, string role, string? title, IReadOnlyCollection<Guid> siteIds, UserId actor, string actorRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(siteIds);
        return db.InTransactionAsync(async ct =>
        {
            if (Refusal(role, actorRole) is { } refused)
            {
                return refused;
            }

            if (await db.Memberships.AnyAsync(m => m.OrgId == organisation && m.UserId == invitee.Value && Live.Contains(m.Status), ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("staff.already_member", "That person is already a member or already invited."));
            }

            OrgMembership membership = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = organisation,
                UserId = invitee.Value,
                OrgRole = role,
                Title = ProviderForms.Blank(title),
                Status = MembershipStatus.Invited,
                InvitedBy = actor.Value,
                InvitedAt = clock.UtcNow,
            };
            db.Memberships.Add(membership);
            await AssignSitesAsync(organisation, membership.Id, siteIds, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("staff.invited", organisation, membership, actor, ct, ("role", role)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);
    }

    public Task<Result> ChangeRoleAsync(Guid organisation, Guid membershipId, string role, UserId actor, string actorRole, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await LiveMembershipAsync(organisation, membershipId, ct).ConfigureAwait(false);
            if (m is null)
            {
                return Result.Failure(Error.NotFound("staff.not_found", "That membership does not exist."));
            }

            // Both the role given and the role taken away must be within the actor's reach.
            if ((Refusal(role, actorRole) ?? Refusal(m.OrgRole, actorRole)) is { } refused)
            {
                return refused;
            }

            if (m.OrgRole == MemberRoles.Owner && role != MemberRoles.Owner && await IsLastOwnerAsync(organisation, m, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("staff.last_owner", "An organisation must keep at least one owner."));
            }

            string from = m.OrgRole;
            m.OrgRole = role;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("staff.role_changed", organisation, m, actor, ct, ("from", from), ("to", role)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    public Task<Result> SetSitesAsync(Guid organisation, Guid membershipId, IReadOnlyCollection<Guid> siteIds, UserId actor, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await LiveMembershipAsync(organisation, membershipId, ct).ConfigureAwait(false);
            if (m is null)
            {
                return Result.Failure(Error.NotFound("staff.not_found", "That membership does not exist."));
            }

            await db.SiteAssignments.Where(a => a.OrgMembershipId == m.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await AssignSitesAsync(organisation, m.Id, siteIds, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("staff.sites_changed", organisation, m, actor, ct, ("sites", siteIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Ends a membership, or withdraws an invitation. The person keeps their account.</summary>
    public Task<Result> RemoveAsync(Guid organisation, Guid membershipId, UserId actor, string actorRole, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await LiveMembershipAsync(organisation, membershipId, ct).ConfigureAwait(false);
            if (m is null)
            {
                return Result.Failure(Error.NotFound("staff.not_found", "That membership does not exist."));
            }

            if (Refusal(m.OrgRole, actorRole) is { } refused)
            {
                return refused;
            }

            if (m.OrgRole == MemberRoles.Owner && await IsLastOwnerAsync(organisation, m, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("staff.last_owner", "An organisation must keep at least one owner."));
            }

            m.Status = MembershipStatus.Ended;
            m.EndedAt = clock.UtcNow;
            m.EndedReason = "removed";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync("staff.removed", organisation, m, actor, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The invitations waiting for <paramref name="user"/>.</summary>
    public Task<IReadOnlyList<Invitation>> InvitationsAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<Invitation>>(async ct =>
            await (from m in db.Memberships.AsNoTracking()
                   join o in db.Organisations.AsNoTracking() on m.OrgId equals o.Id
                   where m.UserId == user.Value && m.Status == MembershipStatus.Invited
                   orderby m.InvitedAt
                   select new Invitation(m.Id, new OrganisationId(o.Id), o.DisplayName, m.OrgRole, m.InvitedAt))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>The invited person's answer. Only their own invitation, only while it is open.</summary>
    public Task<bool> AnswerAsync(UserId user, Guid membershipId, bool accept, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            OrgMembership? m = await db.Memberships
                .FirstOrDefaultAsync(x => x.Id == membershipId && x.UserId == user.Value && x.Status == MembershipStatus.Invited, ct).ConfigureAwait(false);
            if (m is null)
            {
                return false;
            }

            DateTimeOffset now = clock.UtcNow;
            if (accept)
            {
                m.Status = MembershipStatus.Active;
                m.JoinedAt = now;
            }
            else
            {
                m.Status = MembershipStatus.Declined;
                m.EndedAt = now;
                m.EndedReason = "declined";
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(accept ? "staff.invitation_accepted" : "staff.invitation_declined", m.OrgId, m, user, ct).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    /// <summary>Why <paramref name="actorRole"/> may not give or touch <paramref name="role"/>, or null.</summary>
    private static Result? Refusal(string role, string actorRole)
    {
        if (!MemberRoles.Assignable.Contains(role, StringComparer.Ordinal) && role != MemberRoles.Mentor)
        {
            return Result.Failure(Error.Validation("staff.role", "Choose one of the roles."));
        }

        if (role == MemberRoles.Owner && actorRole != MemberRoles.Owner)
        {
            return Result.Failure(Error.Forbidden("staff.owner_only", "Only an owner can make or change an owner."));
        }

        return MemberRoles.Rank(actorRole) < MemberRoles.Rank(MemberRoles.Admin)
            ? Result.Failure(Error.Forbidden("staff.admin_only", "Only an owner or admin can manage staff."))
            : (Result?)null;
    }

    private Task<OrgMembership?> LiveMembershipAsync(Guid organisation, Guid membershipId, CancellationToken ct) =>
        db.Memberships.FirstOrDefaultAsync(m => m.Id == membershipId && m.OrgId == organisation && Live.Contains(m.Status), ct);

    private Task<bool> IsLastOwnerAsync(Guid organisation, OrgMembership m, CancellationToken ct) =>
        db.Memberships.AllAsync(o => o.OrgId != organisation || o.Id == m.Id || o.OrgRole != MemberRoles.Owner || o.Status != MembershipStatus.Active, ct);

    private async Task AssignSitesAsync(Guid organisation, Guid membership, IReadOnlyCollection<Guid> siteIds, CancellationToken ct)
    {
        Guid[] wanted = [.. siteIds.Distinct()];
        List<Guid> ours = await db.Sites.Where(s => s.OrgId == organisation && wanted.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (Guid site in ours)
        {
            db.SiteAssignments.Add(new SiteAssignment { OrgMembershipId = membership, SiteId = site });
        }
    }

    private Task AuditAsync(string action, Guid organisation, OrgMembership m, UserId actor, CancellationToken ct, params (string Key, string Value)[] detail) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            ActorId = actor,
            ActorOrgId = new OrganisationId(organisation),
            SubjectKind = "user",
            SubjectIds = [m.UserId],
            Detail = detail.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
        }, ct);
}
