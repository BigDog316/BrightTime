using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Records;

public sealed record ProjectInput(string? Title, string? Description, Guid? ListingId);

/// <summary>A project as the young person who made it sees it.</summary>
[YouthData]
public sealed record ProjectView(
    Guid Id, string Title, string? Description, Guid? ListingId, string? Activity, string? Organisation,
    string Status, string Visibility, string? ReviewNote, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);

/// <summary>A project waiting for an organisation's approval, named only as the young person names themselves on their portfolio.</summary>
[YouthData]
public sealed record ProjectSubmission(Guid Id, string Title, string? Description, string? Activity, string? DisplayName, DateTimeOffset SubmittedAt);

/// <summary>
/// A young person's projects (M4, D69): something they made, described in words, approved by the
/// organisation of an activity they took part in before anyone else can see it.
/// </summary>
/// <remarks>
/// <para>
/// Draft, submitted, approved, then published by the young person, to anyone with the link or to
/// everyone, once they may showcase their work. Changing an approved or published project makes
/// it a private draft again, so what anyone sees is always what staff approved. The schema holds
/// the same line: a public project is approved and published (<c>ck_project_public_needs_approval</c>).
/// </para>
/// <para>
/// The organisation is found through the young person's own participation record for the
/// project's listing: they can only ask an organisation they took part with.
/// </para>
/// </remarks>
public sealed class Projects(LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, IIdentityQueries identity)
{
    public const string Read = "learning.project.read";
    public const string Changed = "learning.project.changed";
    public const string Reviewed = "learning.project.reviewed";

    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string Approved = "approved";
    public const string Published = "published";
    public const string Removed = "removed";

    public const string Private = "private";

    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal) { "public", "link_only" };

    public Task<IReadOnlyList<ProjectView>> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ProjectView>>(async ct =>
        {
            List<ProjectRecord> projects = await db.Projects.AsNoTracking().Where(p => p.UserId == user.Value && p.Status != Removed)
                .OrderByDescending(p => p.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "project", SubjectIds = [user.Value] }, ct).ConfigureAwait(false);
            return await ViewsAsync(user.Value, projects, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<ProjectView>> CreateAsync(UserId user, ProjectInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await CheckAsync(user, input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<ProjectView>.Failure(invalid);
            }

            DateTimeOffset now = clock.StoredNow();
            ProjectRecord project = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                UserId = user.Value,
                Title = input.Title!.Trim(),
                Status = Draft,
                Visibility = Private,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Apply(project, input);
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, "created", ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Changes a project. One that was submitted, approved or published becomes a private draft again.</summary>
    public Task<Result<ProjectView>> UpdateAsync(UserId user, Guid id, ProjectInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result<ProjectView>.Failure(NotFound());
            }

            if (await CheckAsync(user, input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<ProjectView>.Failure(invalid);
            }

            project.Title = input.Title!.Trim();
            Apply(project, input);
            Unapprove(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, "changed", ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Asks the organisation of the project's activity to approve it.</summary>
    public Task<Result<ProjectView>> SubmitAsync(UserId user, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result<ProjectView>.Failure(NotFound());
            }

            if (project.Status != Draft)
            {
                return Result<ProjectView>.Failure(Error.Conflict("projects.not_draft", "Only a draft is sent for approval."));
            }

            if (project.ListingId is not { } listing || !await TookPartAsync(user.Value, listing, ct).ConfigureAwait(false))
            {
                return Result<ProjectView>.Failure(Error.Validation("projects.activity", "Say which activity it came from: one you took part in."));
            }

            project.Status = Submitted;
            project.ReviewNote = null;
            project.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, Submitted, ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Shows an approved project (<c>public</c> or <c>link_only</c>), or takes it back (<c>private</c>).</summary>
    public Task<Result<ProjectView>> ShowAsync(UserId user, Guid id, string? visibility, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result<ProjectView>.Failure(NotFound());
            }

            if (visibility != Private && (visibility is null || !Shown.Contains(visibility)))
            {
                return Result<ProjectView>.Failure(Error.Validation("projects.visibility", "public, link_only or private."));
            }

            if (project.Status is not (Approved or Published))
            {
                return Result<ProjectView>.Failure(Error.Conflict("projects.not_approved", "A project is shown once it's approved."));
            }

            if (visibility != Private && !(await identity.GetCapabilitiesAsync(user, ct).ConfigureAwait(false)).CanPublishShowcase)
            {
                return Result<ProjectView>.Failure(Error.Forbidden("projects.showcase", "Showing your work outside the platform isn't open to you yet."));
            }

            project.Visibility = visibility;
            project.Status = visibility == Private ? Approved : Published;
            project.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, visibility, ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Takes a project off the portfolio for good.</summary>
    public Task<Result> RemoveAsync(UserId user, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result.Failure(NotFound());
            }

            project.Status = Removed;
            project.Visibility = Private;
            project.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, Removed, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>The projects waiting for this organisation's approval: those about its activities.</summary>
    public Task<IReadOnlyList<ProjectSubmission>> SubmittedAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ProjectSubmission>>(async ct =>
        {
            var waiting = await (from p in db.Projects.AsNoTracking()
                                 join r in db.Participation on new { p.UserId, ListingId = p.ListingId } equals new { r.UserId, r.ListingId }
                                 join lp in db.Profiles on p.UserId equals lp.UserId into profiles
                                 from lp in profiles.DefaultIfEmpty()
                                 where p.Status == Submitted && r.OrgId == org.Value
                                 orderby p.UpdatedAt
                                 select new { p.Id, p.UserId, p.Title, p.Description, Activity = r.TitleSnapshot, Name = lp == null ? null : lp.DisplayName, p.UpdatedAt })
                .Take(500).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Read,
                SubjectKind = "project",
                SubjectIds = [.. waiting.Select(w => w.Id)],
                ActorOrgId = org,
            }, ct).ConfigureAwait(false);
            return [.. waiting.Select(w => new ProjectSubmission(w.Id, w.Title, w.Description, w.Activity, w.Name, w.UpdatedAt))];
        }, cancellationToken);

    /// <summary>Approves a submitted project, or sends it back with a note saying what to change.</summary>
    public Task<Result> ReviewAsync(OrganisationId org, UserId reviewer, Guid id, bool approve, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? said = note?.Trim() is { Length: > 0 } n ? n : null;
            if (said is { Length: > 1000 } || (!approve && said is null))
            {
                return Result.Failure(Error.Validation("projects.review_note", "Say what to change, in up to 1,000 characters."));
            }

            ProjectRecord? project = await (from p in db.Projects
                                            join r in db.Participation on new { p.UserId, ListingId = p.ListingId } equals new { r.UserId, r.ListingId }
                                            where p.Id == id && p.Status == Submitted && r.OrgId == org.Value
                                            select p).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (project is null)
            {
                return Result.Failure(Error.NotFound("projects.nothing_to_review", "No project of your activities is waiting for that."));
            }

            DateTimeOffset now = clock.StoredNow();
            project.Status = approve ? Approved : Draft;
            project.ApprovedBy = approve ? reviewer.Value : null;
            project.ApprovedAt = approve ? now : null;
            project.ReviewNote = approve ? null : said;
            project.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Reviewed, project, org, approve ? "approved" : "sent_back", ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>A young person's projects that are approved or shown, for their export.</summary>
    internal async Task<IReadOnlyList<ProjectView>> ApprovedOfAsync(Guid userId, CancellationToken ct) =>
        await ViewsAsync(userId, await db.Projects.AsNoTracking().Where(p => p.UserId == userId && (p.Status == Approved || p.Status == Published))
            .OrderByDescending(p => p.ApprovedAt).ToListAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private static void Apply(ProjectRecord project, ProjectInput input)
    {
        project.Description = input.Description?.Trim() is { Length: > 0 } d ? d : null;
        project.ListingId = input.ListingId;
    }

    /// <summary>What anyone sees is what staff approved: a change takes it back to a private draft.</summary>
    private void Unapprove(ProjectRecord project)
    {
        project.Status = Draft;
        project.Visibility = Private;
        project.ApprovedBy = null;
        project.ApprovedAt = null;
        project.UpdatedAt = clock.StoredNow();
    }

    private async Task<Error?> CheckAsync(UserId user, ProjectInput input, CancellationToken ct)
    {
        if (input.Title?.Trim() is not { Length: > 0 and <= 200 } || input.Description is { Length: > 10000 })
        {
            return Error.Validation("projects.text", "A title of up to 200 characters, and a description of up to 10,000.");
        }

        return input.ListingId is { } listing && !await TookPartAsync(user.Value, listing, ct).ConfigureAwait(false)
            ? Error.Validation("projects.activity", "Say which activity it came from: one you took part in.")
            : null;
    }

    private Task<bool> TookPartAsync(Guid userId, Guid listingId, CancellationToken ct) =>
        db.Participation.AnyAsync(r => r.UserId == userId && r.ListingId == listingId, ct);

    private Task<ProjectRecord?> OwnAsync(UserId user, Guid id, CancellationToken ct) =>
        db.Projects.FirstOrDefaultAsync(p => p.Id == id && p.UserId == user.Value && p.Status != Removed, ct);

    private async Task<ProjectView> ViewAsync(ProjectRecord project, CancellationToken ct) =>
        (await ViewsAsync(project.UserId, [project], ct).ConfigureAwait(false))[0];

    private async Task<IReadOnlyList<ProjectView>> ViewsAsync(Guid userId, IReadOnlyList<ProjectRecord> projects, CancellationToken ct)
    {
        Guid[] listings = [.. projects.Where(p => p.ListingId is not null).Select(p => p.ListingId!.Value).Distinct()];
        var activities = await db.Participation.AsNoTracking().Where(r => r.UserId == userId && r.ListingId != null && listings.Contains(r.ListingId!.Value))
            .Select(r => new { Listing = r.ListingId!.Value, r.TitleSnapshot, r.OrgNameSnapshot }).ToListAsync(ct).ConfigureAwait(false);
        return [.. projects.Select(p =>
        {
            var activity = p.ListingId is { } l ? activities.FirstOrDefault(a => a.Listing == l) : null;
            return new ProjectView(p.Id, p.Title, p.Description, p.ListingId, activity?.TitleSnapshot, activity?.OrgNameSnapshot,
                p.Status, p.Visibility, p.ReviewNote, p.ApprovedAt, p.UpdatedAt);
        })];
    }

    private Task AuditAsync(string action, ProjectRecord project, OrganisationId? org, string what, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "project",
            SubjectIds = [project.Id, project.UserId],
            ActorOrgId = org,
            Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = what },
        }, ct);

    private static Error NotFound() => Error.NotFound("projects.not_found", "You have no project like that.");
}
