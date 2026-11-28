using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Screening;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts;
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
    string Status, string Visibility, string? ReviewNote, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ProjectPicture> Pictures);

/// <summary>A project waiting for an organisation's approval, named only as the young person names themselves on their portfolio.</summary>
[YouthData]
public sealed record ProjectSubmission(
    Guid Id, string Title, string? Description, string? Activity, string? DisplayName, DateTimeOffset SubmittedAt, IReadOnlyList<ProjectPicture> Pictures);

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
/// <para>
/// Pictures (D104) are private media objects, never served at <c>/media</c>. The young person sees
/// their own; staff see them while the project, or a playlist step it's the evidence for, waits for
/// their review; anyone else only on a project that's published. Adding a picture, or changing what
/// one says, makes the project a private draft again like any other change. Taking one away
/// doesn't: it can only take something away from what staff approved, and should never wait.
/// </para>
/// </remarks>
public sealed class Projects(
    LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, IIdentityQueries identity, IDirectoryQueries directory, IMediaLibrary media,
    WritingScreen screen)
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

            // Staff read it from here on (D118): its words, and every picture's, are screened first.
            ILookup<Guid, ProjectPicture> pictures = await PicturesOfAsync([project.Id], ct).ConfigureAwait(false);
            if (await screen.CheckAsync(db, "project", "project", project.Id,
                    [project.Title, project.Description, .. pictures[project.Id].SelectMany(pic => new[] { pic.Alt, pic.Caption })], ct).ConfigureAwait(false) is { } personal)
            {
                return Result<ProjectView>.Failure(personal);
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

    /// <summary>Takes a project off the portfolio for good, and its pictures with it.</summary>
    public async Task<Result> RemoveAsync(UserId user, Guid id, CancellationToken cancellationToken = default)
    {
        List<Guid> pictures = [];
        Result removed = await db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result.Failure(NotFound());
            }

            List<ProjectMediaRecord> held = await db.ProjectMedia.Where(m => m.ProjectId == id).ToListAsync(ct).ConfigureAwait(false);
            pictures.AddRange(held.Select(m => m.MediaId));
            db.ProjectMedia.RemoveRange(held);
            project.Status = Removed;
            project.Visibility = Private;
            project.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, Removed, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken).ConfigureAwait(false);

        // The bytes are Platform's, in their own transaction: they go once nothing points to them.
        foreach (Guid picture in pictures)
        {
            await media.RemoveAsync(picture, user, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    // ---- pictures (D104) ------------------------------------------------------------------------

    /// <summary>Adds a picture, saying what it shows. The project becomes a private draft again.</summary>
    public async Task<Result<ProjectView>> AddPictureAsync(
        UserId user, Guid id, ReadOnlyMemory<byte> file, string? alt, string? caption, CancellationToken cancellationToken = default)
    {
        Result<(string Alt, string? Caption)> words = ProjectPictures.Words(alt, caption);
        if (!words.IsSuccess)
        {
            return Result<ProjectView>.Failure(words.Error);
        }

        if (await db.InTransactionAsync(ct => RoomAsync(user, id, ct), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return Result<ProjectView>.Failure(refused);
        }

        Result<StoredImage> addedImage = await media.AddImageAsync(file, ProjectPictures.Rules, MediaVisibility.Private, user, cancellationToken).ConfigureAwait(false);
        if (!addedImage.IsSuccess)
        {
            return Result<ProjectView>.Failure(MediaRefusals.Or(addedImage.Error, ProjectPictures.NotAPicture));
        }

        StoredImage image = addedImage.Value;

        Result<ProjectView> added = await db.InTransactionAsync(async ct =>
        {
            // Asked again: another picture may have been added meanwhile.
            if (await RoomAsync(user, id, ct).ConfigureAwait(false) is { } full)
            {
                return Result<ProjectView>.Failure(full);
            }

            ProjectRecord project = (await OwnAsync(user, id, ct).ConfigureAwait(false))!;
            short last = await db.ProjectMedia.Where(m => m.ProjectId == id).Select(m => (short?)m.SortOrder).MaxAsync(ct).ConfigureAwait(false) ?? -1;
            db.ProjectMedia.Add(new ProjectMediaRecord
            {
                ProjectId = id,
                MediaId = image.MediaId,
                AltText = words.Value.Alt,
                Caption = words.Value.Caption,
                SortOrder = (short)(last + 1),
            });
            Unapprove(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, "picture_added", ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (!added.IsSuccess)
        {
            await media.RemoveAsync(image.MediaId, user, cancellationToken).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>Changes what a picture shows, or its caption. The project becomes a private draft again.</summary>
    public Task<Result<ProjectView>> DescribePictureAsync(
        UserId user, Guid id, Guid mediaId, string? alt, string? caption, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Result<(string Alt, string? Caption)> words = ProjectPictures.Words(alt, caption);
            if (!words.IsSuccess)
            {
                return Result<ProjectView>.Failure(words.Error);
            }

            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result<ProjectView>.Failure(NotFound());
            }

            if (await db.ProjectMedia.FirstOrDefaultAsync(m => m.ProjectId == id && m.MediaId == mediaId, ct).ConfigureAwait(false) is not { } picture)
            {
                return Result<ProjectView>.Failure(ProjectPictures.NotFound);
            }

            picture.AltText = words.Value.Alt;
            picture.Caption = words.Value.Caption;
            Unapprove(project);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, "picture_described", ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Takes a picture away, and its bytes with it. The project keeps its approval.</summary>
    public async Task<Result<ProjectView>> RemovePictureAsync(UserId user, Guid id, Guid mediaId, CancellationToken cancellationToken = default)
    {
        Result<ProjectView> removed = await db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(user, id, ct).ConfigureAwait(false) is not { } project)
            {
                return Result<ProjectView>.Failure(NotFound());
            }

            if (await db.ProjectMedia.FirstOrDefaultAsync(m => m.ProjectId == id && m.MediaId == mediaId, ct).ConfigureAwait(false) is not { } picture)
            {
                return Result<ProjectView>.Failure(ProjectPictures.NotFound);
            }

            db.ProjectMedia.Remove(picture);
            project.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, project, null, "picture_removed", ct).ConfigureAwait(false);
            return Result<ProjectView>.Success(await ViewAsync(project, ct).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (removed.IsSuccess)
        {
            await media.RemoveAsync(mediaId, user, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>One of the young person's own pictures; null if it isn't theirs.</summary>
    public async Task<MediaFile?> PictureAsync(UserId user, Guid id, Guid mediaId, CancellationToken cancellationToken = default) =>
        await db.InTransactionAsync(ct => (from m in db.ProjectMedia
                                           join p in db.Projects on m.ProjectId equals p.Id
                                           where m.ProjectId == id && m.MediaId == mediaId && p.UserId == user.Value && p.Status != Removed
                                           select m).AnyAsync(ct), cancellationToken).ConfigureAwait(false)
            ? await media.GetAsync(mediaId, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// A picture for an organisation's staff, while it waits for their review: on a project about
    /// one of its activities (<paramref name="projects"/>), or on the project that's the evidence for
    /// a step of one of its playlists (<paramref name="steps"/>). The caller says which its staff
    /// may review. Null for any other.
    /// </summary>
    public async Task<MediaFile?> ReviewPictureAsync(OrganisationId org, Guid mediaId, bool projects, bool steps, CancellationToken cancellationToken = default)
    {
        bool waiting = await db.InTransactionAsync(ct =>
        {
            IQueryable<ParticipationRecordRecord> ours = db.Participation.Where(r => r.OrgId == org.Value);
            IQueryable<Guid> evidence = from pr in db.PlaylistProgress
                                        join e in db.PlaylistEnrollments on pr.PlaylistEnrollmentId equals e.Id
                                        join pl in db.Playlists on e.PlaylistId equals pl.Id
                                        where pr.State == "submitted" && pl.OrgId == org.Value && pr.EvidenceProjectId != null
                                        select pr.EvidenceProjectId!.Value;
            return (from m in db.ProjectMedia
                    join p in db.Projects on m.ProjectId equals p.Id
                    where m.MediaId == mediaId && p.Status != Removed
                        && ((projects && p.Status == Submitted && ours.Any(r => r.UserId == p.UserId && r.ListingId == p.ListingId))
                            || (steps && evidence.Contains(p.Id)))
                    select m).AnyAsync(ct);
        }, cancellationToken).ConfigureAwait(false);
        return waiting ? await media.GetAsync(mediaId, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>A picture on a published project of a portfolio shown by the link name; null for any other.</summary>
    public async Task<MediaFile?> PublicPictureAsync(string slug, Guid id, Guid mediaId, CancellationToken cancellationToken = default)
    {
        string name = (slug ?? string.Empty).Trim().ToLowerInvariant();
        bool shown = await db.InTransactionAsync(ct => (from m in db.ProjectMedia
                                                        join p in db.Projects on m.ProjectId equals p.Id
                                                        join lp in db.Profiles on p.UserId equals lp.UserId
                                                        where m.ProjectId == id && m.MediaId == mediaId
                                                            && lp.PublicSlug == name && (lp.Visibility == "link_only" || lp.Visibility == "public")
                                                            && p.Status == Published && (p.Visibility == "public" || p.Visibility == "link_only")
                                                        select m).AnyAsync(ct), cancellationToken).ConfigureAwait(false);
        return shown ? await media.GetAsync(mediaId, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>The pictures of these projects, in their order, by project.</summary>
    internal async Task<ILookup<Guid, ProjectPicture>> PicturesOfAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct) =>
        (await db.ProjectMedia.AsNoTracking().Where(m => projectIds.Contains(m.ProjectId)).OrderBy(m => m.SortOrder).ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(m => m.ProjectId, m => new ProjectPicture(m.MediaId, m.AltText, m.Caption));

    private async Task<Error?> RoomAsync(UserId user, Guid id, CancellationToken ct) =>
        await OwnAsync(user, id, ct).ConfigureAwait(false) is null ? NotFound()
        : await db.ProjectMedia.CountAsync(m => m.ProjectId == id, ct).ConfigureAwait(false) >= ProjectPictures.MaxPictures ? ProjectPictures.Full
        : null;

    /// <summary>The projects waiting for this organisation's approval: those about its activities.</summary>
    public Task<IReadOnlyList<ProjectSubmission>> SubmittedAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<ProjectSubmission>>(async ct =>
        {
            // A young person can have more than one record for an activity (a second season, say):
            // the project is asked about once, so the records are tested for, not joined (D78).
            IQueryable<ParticipationRecordRecord> ours = db.Participation.Where(r => r.OrgId == org.Value);
            var waiting = await (from p in db.Projects.AsNoTracking()
                                 where p.Status == Submitted && ours.Any(r => r.UserId == p.UserId && r.ListingId == p.ListingId)
                                 join lp in db.Profiles on p.UserId equals lp.UserId into profiles
                                 from lp in profiles.DefaultIfEmpty()
                                 orderby p.UpdatedAt
                                 select new
                                 {
                                     p.Id,
                                     p.UserId,
                                     p.Title,
                                     p.Description,
                                     Activity = ours.Where(r => r.UserId == p.UserId && r.ListingId == p.ListingId).OrderByDescending(r => r.StartedOn).Select(r => r.TitleSnapshot).FirstOrDefault(),
                                     Name = lp == null ? null : lp.DisplayName,
                                     p.UpdatedAt,
                                 })
                .Take(500).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Read,
                SubjectKind = "project",
                SubjectIds = [.. waiting.Select(w => w.Id)],
                ActorOrgId = org,
            }, ct).ConfigureAwait(false);
            ILookup<Guid, ProjectPicture> pictures = await PicturesOfAsync([.. waiting.Select(w => w.Id)], ct).ConfigureAwait(false);
            return [.. waiting.Select(w => new ProjectSubmission(w.Id, w.Title, w.Description, w.Activity, w.Name, w.UpdatedAt, [.. pictures[w.Id]]))];
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
            string name = await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "";
            await WorkNotices.TellAsync(db, tenant, project.UserId, WorkNotices.WorkReviewedKind,
                approve ? WorkNotices.ProjectApproved : WorkNotices.ProjectSentBack, WorkNotices.Args(("org", name)),
                "/portfolio/projects/" + project.Id.ToString("D"), WorkNotices.Key("project-reviewed", project.Id, now), ct).ConfigureAwait(false);
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
        ILookup<Guid, ProjectPicture> pictures = await PicturesOfAsync([.. projects.Select(p => p.Id)], ct).ConfigureAwait(false);
        return [.. projects.Select(p =>
        {
            var activity = p.ListingId is { } l ? activities.FirstOrDefault(a => a.Listing == l) : null;
            return new ProjectView(p.Id, p.Title, p.Description, p.ListingId, activity?.TitleSnapshot, activity?.OrgNameSnapshot,
                p.Status, p.Visibility, p.ReviewNote, p.ApprovedAt, p.UpdatedAt, [.. pictures[p.Id]]);
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
