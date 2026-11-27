using System.Text.Json;
using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Records;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Playlists;

public sealed record PlaylistInput(
    string? Title, string? Description, bool? IsOrdered, short? AgeMin, short? AgeMax, decimal? EstimatedHours,
    Guid? CompletionBadgeClassId, Guid? RequiresBadgeClassId, short? RequiredStepCount);

public sealed record StepInput(
    string? Kind, string? Title, string? Instructions, Guid? ListingId, string? ExternalUrl, short? ExpectedMinutes, bool? IsRequired, bool? RequiresReview,
    IReadOnlyList<QuizQuestionInput>? Questions = null, short? PassPercent = null);

/// <summary>A quiz question as staff write it (D92): what it asks, two to six answers, and which is right (from 0).</summary>
public sealed record QuizQuestionInput(string? Prompt, IReadOnlyList<string>? Options, int? Correct);

/// <summary>A quiz question as it's read. Which answer is right is there only for the organisation's own staff.</summary>
public sealed record QuizQuestionView(string Prompt, IReadOnlyList<string> Options, int? Correct = null);

/// <summary>
/// One step. An <c>attend_listing</c> step names its activity (D79): its title in the request's
/// language, and its slug while it's published, for a link to it.
/// </summary>
public sealed record StepView(
    Guid Id, int Order, string Kind, string Title, string? Instructions, Guid? ListingId, string? ExternalUrl, short? ExpectedMinutes, bool IsRequired, bool RequiresReview,
    string? ListingTitle = null, string? ListingSlug = null, IReadOnlyList<QuizQuestionView>? Questions = null, short? PassPercent = null);

/// <summary>A playlist as its organisation sees it.</summary>
public sealed record PlaylistView(
    Guid Id, string Slug, string Title, string? Description, bool IsOrdered, short? AgeMin, short? AgeMax, decimal? EstimatedHours,
    Guid? CompletionBadgeClassId, string? CompletionBadge, Guid? RequiresBadgeClassId, string? RequiresBadge, short? RequiredStepCount,
    string Status, IReadOnlyList<StepView> Steps, int Started, int Completed);

/// <summary>A published playlist as anyone sees it.</summary>
public sealed record PublicPlaylist(
    Guid Id, string Slug, string Title, string? Description, string? Organisation, bool IsOrdered, short? AgeMin, short? AgeMax,
    decimal? EstimatedHours, string? CompletionBadge, Guid? CompletionBadgeClassId, string? RequiresBadge, IReadOnlyList<StepView> Steps);

/// <summary>One step of a young person's playlist: where they are on it, and whether it's open to them yet.</summary>
/// <remarks>
/// A quiz (D92) says how many answers the last try got right, and how many tries there were; a try
/// that fell short leaves it <c>in_progress</c>. An upload step (D104) names the project sent as its evidence.
/// </remarks>
[YouthData]
public sealed record MyStep(
    StepView Step, string State, bool Available, string? Reflection, string? ReviewNote, DateTimeOffset? CompletedAt, int? QuizCorrect = null, int QuizAttempts = 0,
    Guid? EvidenceProjectId = null);

[YouthData]
public sealed record MyPlaylist(
    Guid PlaylistId, string Slug, string Title, string? Organisation, bool IsOrdered, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    Guid? AwardedAssertionId, IReadOnlyList<MyStep> Steps, string? Description = null, string? CompletionBadge = null);

/// <summary>A step waiting for review, named only as the young person names themselves on their portfolio.</summary>
/// <remarks>An upload step (D104) carries the project sent as its evidence, with its pictures.</remarks>
[YouthData]
public sealed record StepSubmission(Guid EnrollmentId, Guid StepId, string StepTitle, string? DisplayName, string? Reflection, StepEvidence? Evidence = null);

/// <summary>The project a young person sent as an upload step's evidence (D104).</summary>
[YouthData]
public sealed record StepEvidence(Guid ProjectId, string Title, string? Description, IReadOnlyList<ProjectPicture> Pictures);

/// <summary>
/// Playlists (M4, D67): an organisation's sequence of steps, which a young person works through
/// at their own pace, with some steps reviewed by staff and a badge at the end.
/// </summary>
/// <remarks>
/// <para>
/// A published playlist never changes, because everyone's progress refers to its steps. Steps
/// can't be undone by the young person; only a reviewer reopens one.
/// </para>
/// <para>
/// Completing the last required step completes the playlist, and the completion badge is awarded
/// in the same transaction, signed by the organisation (D66).
/// </para>
/// </remarks>
public sealed partial class Playlists(
    LearningDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IListingDetails listings,
    Badges.Badges badges,
    Infrastructure.Screening.WritingScreen screen)
{
    public const int MaxSteps = 50;
    public const int MinReflection = 20;
    public const int MaxReflection = 2000;

    public const string Defined = "learning.playlist.defined";
    public const string Read = "learning.playlist.read";
    public const string Progressed = "learning.playlist.progressed";
    public const string Reviewed = "learning.playlist.reviewed";

    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    /// <summary>A step whose evidence is a project with pictures (D104). Staff always check it.</summary>
    public const string Upload = "upload_artifact";

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "attend_listing", "external_activity", "reflection", "mentor_checkin", "quiz", Upload };

    public const int MaxQuestions = 20;
    public const int MaxPrompt = 500;
    public const int MaxOption = 200;

    private static readonly JsonSerializerOptions QuizJson = new(JsonSerializerDefaults.Web);

    // ---- the organisation's ----------------------------------------------------------------------

    public Task<IReadOnlyList<PlaylistView>> ListAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<PlaylistView>>(async ct =>
        {
            List<PlaylistRecord> found = await db.Playlists.AsNoTracking().Where(p => p.OrgId == org.Value).OrderBy(p => p.Title).ToListAsync(ct).ConfigureAwait(false);
            List<PlaylistView> views = [];
            foreach (PlaylistRecord p in found)
            {
                views.Add(await ViewAsync(p, ct).ConfigureAwait(false));
            }

            return views;
        }, cancellationToken);

    public Task<Result<PlaylistView>> GetAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnAsync(org, id, ct).ConfigureAwait(false) is { } p
                ? Result<PlaylistView>.Success(await ViewAsync(p, ct).ConfigureAwait(false))
                : Result<PlaylistView>.Failure(NotFound()), cancellationToken);

    /// <summary>A new playlist, as a draft with no steps.</summary>
    public Task<Result<PlaylistView>> CreateAsync(OrganisationId org, UserId author, PlaylistInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await CheckAsync(org, input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<PlaylistView>.Failure(invalid);
            }

            string stem = Slugged(input.Title!);
            List<string> taken = await db.Playlists.AsNoTracking().Where(p => p.TenantId == tenant.TenantId.Value && p.Slug.StartsWith(stem))
                .Select(p => p.Slug).ToListAsync(ct).ConfigureAwait(false);
            string slug = stem;
            for (int n = 2; taken.Contains(slug, StringComparer.OrdinalIgnoreCase); n++)
            {
                slug = $"{stem}-{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            }

            DateTimeOffset now = clock.StoredNow();
            PlaylistRecord playlist = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                OrgId = org.Value,
                Slug = slug,
                Title = input.Title!.Trim(),
                Status = Draft,
                CreatedBy = author.Value,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Apply(playlist, input);
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await SetUnlockAsync(playlist.Id, input.RequiresBadgeClassId, ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [playlist.Id], ct, "created").ConfigureAwait(false);
            return Result<PlaylistView>.Success(await ViewAsync(playlist, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Changes a draft's details.</summary>
    public Task<Result<PlaylistView>> UpdateAsync(OrganisationId org, Guid id, PlaylistInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            Result<PlaylistRecord> draft = await DraftAsync(org, id, ct).ConfigureAwait(false);
            if (!draft.IsSuccess)
            {
                return Result<PlaylistView>.Failure(draft.Error);
            }

            if (await CheckAsync(org, input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<PlaylistView>.Failure(invalid);
            }

            PlaylistRecord playlist = draft.Value;
            playlist.Title = input.Title!.Trim();
            Apply(playlist, input);
            playlist.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await SetUnlockAsync(playlist.Id, input.RequiresBadgeClassId, ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [playlist.Id], ct, "changed").ConfigureAwait(false);
            return Result<PlaylistView>.Success(await ViewAsync(playlist, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Replaces a draft's steps, in order.</summary>
    public Task<Result<PlaylistView>> SetStepsAsync(OrganisationId org, Guid id, IReadOnlyList<StepInput> steps, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(steps);
            Result<PlaylistRecord> draft = await DraftAsync(org, id, ct).ConfigureAwait(false);
            if (!draft.IsSuccess)
            {
                return Result<PlaylistView>.Failure(draft.Error);
            }

            if (steps.Count is 0 or > MaxSteps)
            {
                return Result<PlaylistView>.Failure(Error.Validation("playlists.steps", $"Between 1 and {MaxSteps} steps."));
            }

            List<PlaylistStepRecord> rows = [];
            for (int i = 0; i < steps.Count; i++)
            {
                StepInput s = steps[i];
                Dictionary<string, object> at = new(StringComparer.Ordinal) { ["step"] = i + 1 };
                if (s?.Kind is not { } kind || !Kinds.Contains(kind))
                {
                    return Result<PlaylistView>.Failure(Error.Validation("playlists.step_kind",
                        "Attending a listing, an activity elsewhere, a reflection, a quiz, pictures of their work, or a check-in with staff.", arguments: at));
                }

                if (s.Title?.Trim() is not { Length: > 0 and <= 200 } title || s.Instructions is { Length: > 4000 } || s.ExpectedMinutes is < 1 or > 1440)
                {
                    return Result<PlaylistView>.Failure(Error.Validation("playlists.step", "A title of up to 200 characters, instructions of up to 4,000, and up to a day's minutes.", arguments: at));
                }

                Guid? listingId = null;
                string? url = null;
                string? quiz = null;
                short? pass = null;
                if (kind == "quiz")
                {
                    if (Quiz(s.Questions) is not { } questions || s.PassPercent is < 1 or > 100)
                    {
                        return Result<PlaylistView>.Failure(Error.Validation("playlists.step_quiz",
                            "A quiz of 1 to 20 questions, each with 2 to 6 different answers and the right one marked, and a pass mark from 1 to 100 per cent.", arguments: at));
                    }

                    quiz = JsonSerializer.Serialize(questions, QuizJson);
                    pass = s.PassPercent ?? 100;
                }
                else if (kind == "attend_listing")
                {
                    if (s.ListingId is not { } listing || !await IsPublishedListingAsync(listing, ct).ConfigureAwait(false))
                    {
                        return Result<PlaylistView>.Failure(Error.Validation("playlists.step_listing", "A published listing to attend.", arguments: at));
                    }

                    listingId = listing;
                }
                else if (kind == "external_activity")
                {
                    if (s.ExternalUrl?.Trim() is not { Length: <= 2000 } link || !Uri.TryCreate(link, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps)
                    {
                        return Result<PlaylistView>.Failure(Error.Validation("playlists.step_link", "A link starting with https://.", arguments: at));
                    }

                    url = parsed.ToString();
                }

                rows.Add(new PlaylistStepRecord
                {
                    Id = UuidV7.NewId(),
                    PlaylistId = id,
                    SortOrder = (short)i,
                    StepKind = kind,
                    Title = title,
                    InstructionsMd = s.Instructions?.Trim() is { Length: > 0 } text ? text : null,
                    ListingId = listingId,
                    ExternalUrl = url,
                    ExpectedMinutes = s.ExpectedMinutes,
                    IsRequired = s.IsRequired ?? true,
                    RequiresReview = kind is "mentor_checkin" or Upload || (s.RequiresReview ?? false),
                    Quiz = quiz,
                    QuizPassPercent = pass,
                });
            }

            await db.PlaylistSteps.Where(x => x.PlaylistId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.PlaylistSteps.AddRange(rows);
            draft.Value.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [id], ct, "steps").ConfigureAwait(false);
            return Result<PlaylistView>.Success(await ViewAsync(draft.Value, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Opens a draft to young people. From now on it never changes.</summary>
    public Task<Result<PlaylistView>> PublishAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Result<PlaylistRecord> draft = await DraftAsync(org, id, ct).ConfigureAwait(false);
            if (!draft.IsSuccess)
            {
                return Result<PlaylistView>.Failure(draft.Error);
            }

            PlaylistRecord playlist = draft.Value;
            List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == id).ToListAsync(ct).ConfigureAwait(false);
            if (!steps.Any(s => s.IsRequired) || playlist.RequiredStepCount > steps.Count)
            {
                return Result<PlaylistView>.Failure(Error.Validation("playlists.unpublishable",
                    "Give it at least one required step, and no more steps to complete than it has."));
            }

            if (playlist.CompletionBadgeClassId is { } badge && await OwnBadgeAsync(org, badge, ct).ConfigureAwait(false) is null)
            {
                return Result<PlaylistView>.Failure(Error.Validation("playlists.badge", "A published badge of your organisation, or none."));
            }

            playlist.Status = Published;
            playlist.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [id], ct, Published).ConfigureAwait(false);
            return Result<PlaylistView>.Success(await ViewAsync(playlist, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Stops anyone new starting. Those already on it carry on.</summary>
    public Task<Result<PlaylistView>> ArchiveAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } playlist)
            {
                return Result<PlaylistView>.Failure(NotFound());
            }

            if (playlist.Status != Published)
            {
                return Result<PlaylistView>.Failure(Error.Conflict("playlists.not_published", "This playlist isn't published."));
            }

            playlist.Status = Archived;
            playlist.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Defined, org, [id], ct, Archived).ConfigureAwait(false);
            return Result<PlaylistView>.Success(await ViewAsync(playlist, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>The steps waiting for review on one of the organisation's playlists.</summary>
    public Task<Result<IReadOnlyList<StepSubmission>>> SubmissionsAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await OwnAsync(org, id, ct).ConfigureAwait(false) is null)
            {
                return Result<IReadOnlyList<StepSubmission>>.Failure(NotFound());
            }

            var waiting = await (from pr in db.PlaylistProgress.AsNoTracking()
                                 join e in db.PlaylistEnrollments on pr.PlaylistEnrollmentId equals e.Id
                                 join s in db.PlaylistSteps on pr.StepId equals s.Id
                                 join lp in db.Profiles on e.UserId equals lp.UserId into profiles
                                 from lp in profiles.DefaultIfEmpty()
                                 where e.PlaylistId == id && pr.State == "submitted"
                                 orderby e.StartedAt, s.SortOrder
                                 select new { e.Id, e.UserId, StepId = s.Id, s.Title, Name = lp == null ? null : lp.DisplayName, pr.Reflection, pr.EvidenceProjectId })
                .Take(500).ToListAsync(ct).ConfigureAwait(false);
            Dictionary<Guid, StepEvidence> evidence = await EvidenceAsync([.. waiting.Select(w => w.EvidenceProjectId).OfType<Guid>()], ct).ConfigureAwait(false);
            await AuditAsync(Read, org, [.. waiting.Select(w => w.Id).Distinct()], ct, "submissions").ConfigureAwait(false);
            return Result<IReadOnlyList<StepSubmission>>.Success([.. waiting.Select(w => new StepSubmission(w.Id, w.StepId, w.Title, w.Name, w.Reflection,
                w.EvidenceProjectId is { } project ? evidence.GetValueOrDefault(project) : null))]);
        }, cancellationToken);

    /// <summary>Approves a submitted step, or sends it back with a note. Approving may complete the playlist.</summary>
    public Task<Result> ReviewAsync(OrganisationId org, UserId reviewer, Uri site, Guid playlistId, Guid stepId, Guid enrollmentId, bool approve, string? note, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            string? said = note?.Trim() is { Length: > 0 } n ? n : null;
            if (said is { Length: > 1000 } || (!approve && said is null))
            {
                return Result.Failure(Error.Validation("playlists.review_note", "Say what to do again, in up to 1,000 characters."));
            }

            if (await OwnAsync(org, playlistId, ct).ConfigureAwait(false) is not { } playlist)
            {
                return Result.Failure(NotFound());
            }

            PlaylistStepProgressRecord? progress = await (from pr in db.PlaylistProgress
                                                          join e in db.PlaylistEnrollments on pr.PlaylistEnrollmentId equals e.Id
                                                          where e.Id == enrollmentId && e.PlaylistId == playlistId && pr.StepId == stepId
                                                          select pr).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (progress?.State != "submitted")
            {
                return Result.Failure(Error.NotFound("playlists.nothing_to_review", "Nothing is waiting for review there."));
            }

            DateTimeOffset now = clock.StoredNow();
            progress.State = approve ? "completed" : "needs_rework";
            progress.CompletedAt = approve ? now : null;
            progress.ReviewedBy = reviewer.Value;
            progress.ReviewedAt = now;
            progress.ReviewNote = said;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            PlaylistEnrollmentRecord enrollment = await db.PlaylistEnrollments.FirstAsync(e => e.Id == enrollmentId, ct).ConfigureAwait(false);
            await AuditAsync(Reviewed, org, [enrollmentId, enrollment.UserId], ct, approve ? "approved" : "needs_rework").ConfigureAwait(false);
            bool open = enrollment.CompletedAt is null;
            if (approve)
            {
                await AttendedStepsAsync(playlist, enrollment, ct).ConfigureAwait(false);
                await CompleteIfDoneAsync(playlist, enrollment, site, ct).ConfigureAwait(false);
            }

            // The last step checked is told as the playlist finished; its badge has a notice of its own.
            string name = await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "";
            string key = !approve ? WorkNotices.StepSentBack : open && enrollment.CompletedAt is not null ? WorkNotices.PlaylistCompleted : WorkNotices.StepApproved;
            await WorkNotices.TellAsync(db, tenant, enrollment.UserId, WorkNotices.WorkReviewedKind, key,
                WorkNotices.Args(("org", name), ("playlist", playlist.Title)), "/playlists/" + playlist.Slug,
                WorkNotices.Key("step-reviewed:" + enrollmentId.ToString("N"), stepId, now), ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- attendance (D91) --------------------------------------------------------------------------

    /// <summary>
    /// Counts an enrolment's attendance towards the young person's playlists (D91): every open step
    /// to attend an activity they've now attended is done, as "Mark it done" would do it, in order,
    /// so an ordered playlist moves on one step at a time. A step staff check goes to them for
    /// review instead. A playlist finished this way completes and awards its badge.
    /// </summary>
    /// <remarks>
    /// Runs on the worker, after the participation projection; a redelivered message finds the
    /// steps done and changes nothing. Attendance taken away (a correction) never undoes a step:
    /// only a reviewer reopens one.
    /// </remarks>
    public Task CountAttendanceAsync(Guid enrollmentId, Uri site, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            Guid? who = await db.Participation.AsNoTracking().Where(p => p.EnrollmentId == enrollmentId && p.SessionsAttended > 0)
                .Select(p => (Guid?)p.UserId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (who is not { } user)
            {
                return false;
            }

            var open = await (from e in db.PlaylistEnrollments
                              join p in db.Playlists on e.PlaylistId equals p.Id
                              where e.UserId == user && e.CompletedAt == null
                              select new { e, p }).ToListAsync(ct).ConfigureAwait(false);
            foreach (var mine in open)
            {
                if (await AttendedStepsAsync(mine.p, mine.e, ct).ConfigureAwait(false))
                {
                    await CompleteIfDoneAsync(mine.p, mine.e, site, ct).ConfigureAwait(false);
                }
            }

            return true;
        }, cancellationToken);

    // ---- anyone's ----------------------------------------------------------------------------------

    public Task<IReadOnlyList<PublicPlaylist>> PublishedAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<PublicPlaylist>>(async ct =>
        {
            List<PlaylistRecord> found = await db.Playlists.AsNoTracking().Where(p => p.Status == Published).OrderBy(p => p.Title).Take(200).ToListAsync(ct).ConfigureAwait(false);
            List<PublicPlaylist> views = [];
            foreach (PlaylistRecord p in found)
            {
                views.Add(await PublicViewAsync(p, ct).ConfigureAwait(false));
            }

            return views;
        }, cancellationToken);

    public Task<PublicPlaylist?> PublishedAsync(string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<PublicPlaylist?>(async ct =>
            await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Slug == slug && p.Status == Published, ct).ConfigureAwait(false) is { } p
                ? await PublicViewAsync(p, ct).ConfigureAwait(false)
                : null, cancellationToken);

    // ---- the young person's ------------------------------------------------------------------------

    /// <summary>Starts a published playlist. Starting one already started returns it.</summary>
    public Task<Result<MyPlaylist>> EnrollAsync(UserId user, Guid playlistId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playlistId && p.Status != Draft, ct).ConfigureAwait(false) is not { } playlist)
            {
                return Result<MyPlaylist>.Failure(NotFound());
            }

            if (await db.PlaylistEnrollments.AsNoTracking().FirstOrDefaultAsync(e => e.PlaylistId == playlistId && e.UserId == user.Value, ct).ConfigureAwait(false) is { } existing)
            {
                return Result<MyPlaylist>.Success(await MineAsync(playlist, existing, ct).ConfigureAwait(false));
            }

            if (playlist.Status != Published)
            {
                return Result<MyPlaylist>.Failure(Error.Conflict("playlists.not_published", "This playlist isn't published."));
            }

            if (!(await identity.GetCapabilitiesAsync(user, ct).ConfigureAwait(false)).CanSelfEnroll)
            {
                return Result<MyPlaylist>.Failure(Error.Forbidden("playlists.cannot_start", "Your account can't start playlists yet."));
            }

            DateTimeOffset now = clock.StoredNow();
            List<Guid> needed = await db.UnlockRules.AsNoTracking().Where(u => u.UnlocksPlaylistId == playlistId).Select(u => u.BadgeClassId).ToListAsync(ct).ConfigureAwait(false);
            if (needed.Count > 0 && !await db.BadgeAssertions.AnyAsync(a => needed.Contains(a.BadgeClassId) && a.RecipientUserId == user.Value
                                                                             && a.RevokedAt == null && (a.ExpiresOn == null || a.ExpiresOn > now), ct).ConfigureAwait(false))
            {
                return Result<MyPlaylist>.Failure(Error.Forbidden("playlists.locked", "This playlist opens to those who hold the badge it asks for."));
            }

            PlaylistEnrollmentRecord enrollment = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                PlaylistId = playlistId,
                UserId = user.Value,
                StartedAt = now,
            };
            db.PlaylistEnrollments.Add(enrollment);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Progressed, SubjectKind = "playlist_enrollment", SubjectIds = [enrollment.Id], Detail = What("started") }, ct).ConfigureAwait(false);
            return Result<MyPlaylist>.Success(await MineAsync(playlist, enrollment, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>The playlists a young person has started, newest first.</summary>
    public Task<IReadOnlyList<MyPlaylist>> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<MyPlaylist>>(async ct =>
        {
            var started = await (from e in db.PlaylistEnrollments.AsNoTracking()
                                 join p in db.Playlists on e.PlaylistId equals p.Id
                                 where e.UserId == user.Value
                                 orderby e.StartedAt descending
                                 select new { e, p }).ToListAsync(ct).ConfigureAwait(false);
            List<MyPlaylist> mine = [];
            foreach (var s in started)
            {
                mine.Add(await MineAsync(s.p, s.e, ct).ConfigureAwait(false));
            }

            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "playlist_enrollment", SubjectIds = [.. started.Select(s => s.e.Id)] }, ct).ConfigureAwait(false);
            return mine;
        }, cancellationToken);

    /// <summary>
    /// Marks a step done: completed, or submitted when it needs review. A reflection step takes the
    /// reflection; attending a listing needs a record of having attended it. Completing the last
    /// required step completes the playlist and awards its badge.
    /// </summary>
    public Task<Result<MyPlaylist>> DoStepAsync(UserId user, Uri site, Guid playlistId, Guid stepId, string? reflection, CancellationToken cancellationToken = default) =>
        DoStepAsync(user, site, playlistId, stepId, reflection, null, cancellationToken);

    /// <summary>Marks a step done; a quiz's <paramref name="answers"/> (D92) are the chosen option of each question, in order, from 0.</summary>
    public Task<Result<MyPlaylist>> DoStepAsync(
        UserId user, Uri site, Guid playlistId, Guid stepId, string? reflection, IReadOnlyList<int>? answers, CancellationToken cancellationToken = default) =>
        DoStepAsync(user, site, playlistId, stepId, reflection, answers, null, cancellationToken);

    /// <summary>
    /// Marks a step done. An upload step's <paramref name="projectId"/> (D104) is one of the young
    /// person's own projects with at least one picture, sent to staff as its evidence.
    /// </summary>
    public Task<Result<MyPlaylist>> DoStepAsync(
        UserId user, Uri site, Guid playlistId, Guid stepId, string? reflection, IReadOnlyList<int>? answers, Guid? projectId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(site);
            var found = await (from e in db.PlaylistEnrollments
                               join p in db.Playlists on e.PlaylistId equals p.Id
                               where e.PlaylistId == playlistId && e.UserId == user.Value
                               select new { e, p }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                return Result<MyPlaylist>.Failure(Error.NotFound("playlists.not_started", "Start this playlist first."));
            }

            List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == playlistId).OrderBy(s => s.SortOrder).ToListAsync(ct).ConfigureAwait(false);
            if (steps.FirstOrDefault(s => s.Id == stepId) is not { } step)
            {
                return Result<MyPlaylist>.Failure(Error.NotFound("playlists.step_not_found", "This playlist has no step like that."));
            }

            Dictionary<Guid, PlaylistStepProgressRecord> progress = await db.PlaylistProgress.Where(pr => pr.PlaylistEnrollmentId == found.e.Id)
                .ToDictionaryAsync(pr => pr.StepId, ct).ConfigureAwait(false);
            PlaylistStepProgressRecord? mine = progress.GetValueOrDefault(stepId);
            if (mine?.State == "completed")
            {
                return Result<MyPlaylist>.Failure(Error.Conflict("playlists.step_done", "This step is already done."));
            }

            if (mine?.State == "submitted")
            {
                return Result<MyPlaylist>.Failure(Error.Conflict("playlists.awaiting_review", "This step is waiting for review."));
            }

            if (!Available(found.p, steps, progress, step))
            {
                return Result<MyPlaylist>.Failure(Error.Conflict("playlists.out_of_order", "Finish the steps before this one first."));
            }

            string? written = reflection?.Trim() is { Length: > 0 } r ? r : null;
            if (step.StepKind == "reflection" && written is not { Length: >= MinReflection and <= MaxReflection })
            {
                return Result<MyPlaylist>.Failure(Error.Validation("playlists.reflection", "Write between 20 and 2,000 characters."));
            }

            if (written is { Length: > MaxReflection })
            {
                return Result<MyPlaylist>.Failure(Error.Validation("playlists.reflection", "Write between 20 and 2,000 characters."));
            }

            // Staff read a reflection (D118): screened before it's sent.
            if (written is not null && await screen.CheckAsync(db, "reflection", "playlist_step", stepId, [written], ct).ConfigureAwait(false) is { } personal)
            {
                return Result<MyPlaylist>.Failure(personal);
            }

            if (step.StepKind == "attend_listing"
                && !await db.Participation.AnyAsync(pr => pr.UserId == user.Value && pr.ListingId == step.ListingId && pr.SessionsAttended > 0, ct).ConfigureAwait(false))
            {
                return Result<MyPlaylist>.Failure(Error.Conflict("playlists.not_attended", "This step is done once your attendance at the activity is recorded."));
            }

            // An upload step's evidence (D104): a project of their own, with something to see.
            if (step.StepKind == Upload
                && (projectId is not { } evidence
                    || !await db.Projects.AnyAsync(pj => pj.Id == evidence && pj.UserId == user.Value && pj.Status != Projects.Removed
                        && db.ProjectMedia.Any(m => m.ProjectId == pj.Id), ct).ConfigureAwait(false)))
            {
                return Result<MyPlaylist>.Failure(Error.Validation("playlists.evidence", "Choose one of your projects with at least one picture."));
            }

            // A quiz is marked here (D92): every question answered, and enough right to pass.
            int? right = null;
            if (step.StepKind == "quiz")
            {
                IReadOnlyList<QuizQuestionView> questions = Questions(step.Quiz, true);
                if (answers is null || answers.Count != questions.Count || answers.Where((a, i) => a < 0 || a >= questions[i].Options.Count).Any())
                {
                    return Result<MyPlaylist>.Failure(Error.Validation("playlists.quiz_answers", "Answer every question."));
                }

                right = answers.Where((a, i) => a == questions[i].Correct).Count();
            }

            DateTimeOffset now = clock.StoredNow();
            if (mine is null)
            {
                mine = new PlaylistStepProgressRecord { PlaylistEnrollmentId = found.e.Id, StepId = stepId, State = "not_started" };
                db.PlaylistProgress.Add(mine);
                progress[stepId] = mine;
            }

            if (right is { } got)
            {
                mine.QuizCorrect = (short)got;
                mine.QuizAttempts++;
                if (got < Needed(Questions(step.Quiz, false).Count, step.QuizPassPercent))
                {
                    // Short of the pass mark: kept as a try, open for another.
                    mine.State = "in_progress";
                    mine.Reflection = written;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Progressed, SubjectKind = "playlist_enrollment", SubjectIds = [found.e.Id], Detail = What("quiz_tried") }, ct).ConfigureAwait(false);
                    return Result<MyPlaylist>.Success(await MineAsync(found.p, found.e, ct).ConfigureAwait(false));
                }
            }

            mine.State = step.RequiresReview ? "submitted" : "completed";
            mine.CompletedAt = step.RequiresReview ? null : now;
            mine.Reflection = written;
            mine.ReviewNote = null;
            mine.EvidenceProjectId = step.StepKind == Upload ? projectId : null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Progressed, SubjectKind = "playlist_enrollment", SubjectIds = [found.e.Id], Detail = What(mine.State) }, ct).ConfigureAwait(false);
            if (mine.State == "completed")
            {
                await AttendedStepsAsync(found.p, found.e, ct).ConfigureAwait(false);
                await CompleteIfDoneAsync(found.p, found.e, site, ct).ConfigureAwait(false);
            }

            return Result<MyPlaylist>.Success(await MineAsync(found.p, found.e, ct).ConfigureAwait(false));
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>Completes the playlist once every required step is done, and awards its badge.</summary>
    private async Task CompleteIfDoneAsync(PlaylistRecord playlist, PlaylistEnrollmentRecord enrollment, Uri site, CancellationToken ct)
    {
        if (enrollment.CompletedAt is not null)
        {
            return;
        }

        List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == playlist.Id).ToListAsync(ct).ConfigureAwait(false);
        HashSet<Guid> done = [.. await db.PlaylistProgress.AsNoTracking().Where(pr => pr.PlaylistEnrollmentId == enrollment.Id && pr.State == "completed")
            .Select(pr => pr.StepId).ToListAsync(ct).ConfigureAwait(false)];
        if (!steps.Where(s => s.IsRequired).All(s => done.Contains(s.Id)) || done.Count < (playlist.RequiredStepCount ?? 0))
        {
            return;
        }

        PlaylistEnrollmentRecord tracked = await db.PlaylistEnrollments.FirstAsync(e => e.Id == enrollment.Id, ct).ConfigureAwait(false);
        tracked.CompletedAt = clock.StoredNow();
        if (playlist.CompletionBadgeClassId is { } badge && playlist.OrgId is { } org)
        {
            BadgeAssertionRecord? award = await badges.AwardForPlaylistAsync(new OrganisationId(org), site, badge, tracked.UserId, tracked.Id, playlist.Title, ct).ConfigureAwait(false);
            tracked.AwardedAssertionId = award?.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        enrollment.CompletedAt = tracked.CompletedAt;
        enrollment.AwardedAssertionId = tracked.AwardedAssertionId;
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = Progressed,
            SubjectKind = "playlist_enrollment",
            SubjectIds = [tracked.Id, tracked.UserId],
            ActorOrgId = playlist.OrgId is { } o ? new OrganisationId(o) : null,
            Detail = What("completed"),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>In an ordered playlist, a step opens once every required step before it is completed.</summary>
    /// <summary>
    /// Every open, available step to attend an activity the young person has attended, done: in
    /// order, so one done can free the next. Steps sent back, or waiting for review, are left as
    /// they are. True when one was completed (not only sent for review).
    /// </summary>
    private async Task<bool> AttendedStepsAsync(PlaylistRecord playlist, PlaylistEnrollmentRecord enrollment, CancellationToken ct)
    {
        List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == playlist.Id).OrderBy(s => s.SortOrder).ToListAsync(ct).ConfigureAwait(false);
        Guid[] wanted = [.. steps.Where(s => s.StepKind == "attend_listing").Select(s => s.ListingId).OfType<Guid>().Distinct()];
        if (wanted.Length == 0)
        {
            return false;
        }

        HashSet<Guid> attended = [.. await db.Participation.AsNoTracking()
            .Where(p => p.UserId == enrollment.UserId && p.SessionsAttended > 0 && p.ListingId != null && wanted.Contains(p.ListingId.Value))
            .Select(p => p.ListingId!.Value).ToListAsync(ct).ConfigureAwait(false)];
        if (attended.Count == 0)
        {
            return false;
        }

        Dictionary<Guid, PlaylistStepProgressRecord> progress = await db.PlaylistProgress.Where(pr => pr.PlaylistEnrollmentId == enrollment.Id)
            .ToDictionaryAsync(pr => pr.StepId, ct).ConfigureAwait(false);
        DateTimeOffset now = clock.StoredNow();
        List<string> moved = [];
        foreach (PlaylistStepRecord step in steps)
        {
            PlaylistStepProgressRecord? done = progress.GetValueOrDefault(step.Id);
            if (step.StepKind != "attend_listing" || step.ListingId is not { } listing || !attended.Contains(listing)
                || done?.State is "completed" or "submitted" or "needs_rework" || !Available(playlist, steps, progress, step))
            {
                continue;
            }

            if (done is null)
            {
                done = new PlaylistStepProgressRecord { PlaylistEnrollmentId = enrollment.Id, StepId = step.Id, State = "not_started" };
                db.PlaylistProgress.Add(done);
                progress[step.Id] = done;
            }

            done.State = step.RequiresReview ? "submitted" : "completed";
            done.CompletedAt = step.RequiresReview ? null : now;
            moved.Add(done.State);
        }

        if (moved.Count == 0)
        {
            return false;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = Progressed,
            SubjectKind = "playlist_enrollment",
            SubjectIds = [enrollment.Id, enrollment.UserId],
            ActorOrgId = playlist.OrgId is { } org ? new OrganisationId(org) : null,
            Detail = What("attended"),
        }, ct).ConfigureAwait(false);
        return moved.Contains("completed");
    }

    /// <summary>The projects sent as evidence (D104), with their pictures, by project.</summary>
    private async Task<Dictionary<Guid, StepEvidence>> EvidenceAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0)
        {
            return [];
        }

        var projects = await db.Projects.AsNoTracking().Where(pj => projectIds.Contains(pj.Id) && pj.Status != Projects.Removed)
            .Select(pj => new { pj.Id, pj.Title, pj.Description }).ToListAsync(ct).ConfigureAwait(false);
        ILookup<Guid, ProjectPicture> pictures = (await db.ProjectMedia.AsNoTracking().Where(m => projectIds.Contains(m.ProjectId)).OrderBy(m => m.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false)).ToLookup(m => m.ProjectId, m => new ProjectPicture(m.MediaId, m.AltText, m.Caption));
        return projects.ToDictionary(pj => pj.Id, pj => new StepEvidence(pj.Id, pj.Title, pj.Description, [.. pictures[pj.Id]]));
    }

    private static bool Available(PlaylistRecord playlist, IReadOnlyList<PlaylistStepRecord> steps, IReadOnlyDictionary<Guid, PlaylistStepProgressRecord> progress, PlaylistStepRecord step) =>
        !playlist.IsOrdered || steps.Where(s => s.SortOrder < step.SortOrder && s.IsRequired).All(s => progress.GetValueOrDefault(s.Id)?.State == "completed");

    private async Task<MyPlaylist> MineAsync(PlaylistRecord playlist, PlaylistEnrollmentRecord enrollment, CancellationToken ct)
    {
        List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == playlist.Id).OrderBy(s => s.SortOrder).ToListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, PlaylistStepProgressRecord> progress = await db.PlaylistProgress.AsNoTracking().Where(pr => pr.PlaylistEnrollmentId == enrollment.Id)
            .ToDictionaryAsync(pr => pr.StepId, ct).ConfigureAwait(false);
        string? organisation = playlist.OrgId is { } org ? await directory.GetOrganisationNameAsync(new OrganisationId(org), ct).ConfigureAwait(false) : null;
        Dictionary<Guid, StepView> views = (await StepsAsync(steps, ct).ConfigureAwait(false)).ToDictionary(v => v.Id);
        return new MyPlaylist(playlist.Id, playlist.Slug, playlist.Title, organisation, playlist.IsOrdered, enrollment.StartedAt, enrollment.CompletedAt,
            enrollment.AwardedAssertionId,
            [.. steps.Select(s => progress.GetValueOrDefault(s.Id) is { } p
                ? new MyStep(views[s.Id], p.State, Available(playlist, steps, progress, s), p.Reflection, p.ReviewNote, p.CompletedAt, p.QuizCorrect, p.QuizAttempts, p.EvidenceProjectId)
                : new MyStep(views[s.Id], "not_started", Available(playlist, steps, progress, s), null, null, null))],
            playlist.Description, await BadgeNameAsync(playlist.CompletionBadgeClassId, ct).ConfigureAwait(false));
    }

    private async Task<PlaylistView> ViewAsync(PlaylistRecord p, CancellationToken ct)
    {
        List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == p.Id).OrderBy(s => s.SortOrder).ToListAsync(ct).ConfigureAwait(false);
        Guid? requires = await RequiredBadgeAsync(p.Id, ct).ConfigureAwait(false);
        int started = await db.PlaylistEnrollments.CountAsync(e => e.PlaylistId == p.Id, ct).ConfigureAwait(false);
        int completed = await db.PlaylistEnrollments.CountAsync(e => e.PlaylistId == p.Id && e.CompletedAt != null, ct).ConfigureAwait(false);
        return new PlaylistView(p.Id, p.Slug, p.Title, p.Description, p.IsOrdered, p.AgeMin, p.AgeMax, p.EstimatedHours,
            p.CompletionBadgeClassId, await BadgeNameAsync(p.CompletionBadgeClassId, ct).ConfigureAwait(false),
            requires, await BadgeNameAsync(requires, ct).ConfigureAwait(false), p.RequiredStepCount, p.Status, await StepsAsync(steps, ct, answers: true).ConfigureAwait(false), started, completed);
    }

    private async Task<PublicPlaylist> PublicViewAsync(PlaylistRecord p, CancellationToken ct)
    {
        List<PlaylistStepRecord> steps = await db.PlaylistSteps.AsNoTracking().Where(s => s.PlaylistId == p.Id).OrderBy(s => s.SortOrder).ToListAsync(ct).ConfigureAwait(false);
        string? organisation = p.OrgId is { } org ? await directory.GetOrganisationNameAsync(new OrganisationId(org), ct).ConfigureAwait(false) : null;
        return new PublicPlaylist(p.Id, p.Slug, p.Title, p.Description, organisation, p.IsOrdered, p.AgeMin, p.AgeMax, p.EstimatedHours,
            await BadgeNameAsync(p.CompletionBadgeClassId, ct).ConfigureAwait(false), p.CompletionBadgeClassId,
            await BadgeNameAsync(await RequiredBadgeAsync(p.Id, ct).ConfigureAwait(false), ct).ConfigureAwait(false), await StepsAsync(steps, ct).ConfigureAwait(false));
    }

    /// <summary>The steps in order, each activity to attend named once however many steps name it.</summary>
    private async Task<IReadOnlyList<StepView>> StepsAsync(IReadOnlyList<PlaylistStepRecord> steps, CancellationToken ct, bool answers = false)
    {
        Dictionary<Guid, (string Title, string? Slug)> named = [];
        foreach (Guid id in steps.Where(s => s.StepKind == "attend_listing").Select(s => s.ListingId).OfType<Guid>().Distinct())
        {
            if (await listings.GetForReviewAsync(tenant.TenantId, id, tenant.Locale.Code, ct).ConfigureAwait(false) is { } listing)
            {
                bool shown = await listings.GetPublishedAsync(tenant.TenantId, listing.Slug, tenant.Locale.Code, ct).ConfigureAwait(false) is not null;
                named[id] = (listing.Title, shown ? listing.Slug : null);
            }
        }

        return [.. steps.Select(s =>
        {
            (string Title, string? Slug)? listing = s.ListingId is { } id && named.TryGetValue(id, out (string, string?) n) ? n : null;
            return new StepView(s.Id, s.SortOrder + 1, s.StepKind, s.Title, s.InstructionsMd, s.ListingId, s.ExternalUrl, s.ExpectedMinutes, s.IsRequired, s.RequiresReview,
                listing?.Title, listing?.Slug, s.StepKind == "quiz" ? Questions(s.Quiz, answers) : null, s.QuizPassPercent);
        })];
    }

    /// <summary>A quiz as written, checked: 1 to 20 questions, each with 2 to 6 different answers and the right one; null if it isn't.</summary>
    private static List<QuizQuestionView>? Quiz(IReadOnlyList<QuizQuestionInput>? questions)
    {
        if (questions is not { Count: > 0 and <= MaxQuestions })
        {
            return null;
        }

        List<QuizQuestionView> quiz = [];
        foreach (QuizQuestionInput q in questions)
        {
            string[] options = [.. (q?.Options ?? []).Select(o => o?.Trim() ?? string.Empty)];
            if (q?.Prompt?.Trim() is not { Length: > 0 and <= MaxPrompt } prompt
                || options.Length is < 2 or > 6 || options.Any(o => o.Length is 0 or > MaxOption)
                || options.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() != options.Length
                || q.Correct is not { } correct || correct < 0 || correct >= options.Length)
            {
                return null;
            }

            quiz.Add(new QuizQuestionView(prompt, options, correct));
        }

        return quiz;
    }

    /// <summary>A stored quiz's questions; with the right answers only when asked for (the organisation's own view, and marking).</summary>
    private static IReadOnlyList<QuizQuestionView> Questions(string? quiz, bool answers) =>
        quiz is null ? [] : [.. (JsonSerializer.Deserialize<List<QuizQuestionView>>(quiz, QuizJson) ?? [])
            .Select(q => answers ? q : q with { Correct = null })];

    /// <summary>How many right answers pass: the pass mark's share of the questions, rounded up.</summary>
    internal static int Needed(int questions, short? passPercent) =>
        (int)Math.Ceiling(questions * (passPercent ?? 100) / 100m);

    private Task<Guid?> RequiredBadgeAsync(Guid playlistId, CancellationToken ct) =>
        db.UnlockRules.AsNoTracking().Where(u => u.UnlocksPlaylistId == playlistId).Select(u => (Guid?)u.BadgeClassId).FirstOrDefaultAsync(ct);

    private async Task<string?> BadgeNameAsync(Guid? id, CancellationToken ct) =>
        id is { } badge ? await db.BadgeClasses.AsNoTracking().Where(c => c.Id == badge).Select(c => c.Name).FirstOrDefaultAsync(ct).ConfigureAwait(false) : null;

    private async Task SetUnlockAsync(Guid playlistId, Guid? badgeClassId, CancellationToken ct)
    {
        await db.UnlockRules.Where(u => u.UnlocksPlaylistId == playlistId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (badgeClassId is { } badge)
        {
            db.UnlockRules.Add(new UnlockRuleRecord { Id = UuidV7.NewId(), TenantId = tenant.TenantId.Value, BadgeClassId = badge, UnlocksKind = "playlist", UnlocksPlaylistId = playlistId });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void Apply(PlaylistRecord playlist, PlaylistInput input)
    {
        playlist.Description = input.Description?.Trim() is { Length: > 0 } d ? d : null;
        playlist.IsOrdered = input.IsOrdered ?? true;
        playlist.AgeMin = input.AgeMin;
        playlist.AgeMax = input.AgeMax;
        playlist.EstimatedHours = input.EstimatedHours;
        playlist.CompletionBadgeClassId = input.CompletionBadgeClassId;
        playlist.RequiredStepCount = input.RequiredStepCount;
    }

    private async Task<Error?> CheckAsync(OrganisationId org, PlaylistInput input, CancellationToken ct)
    {
        if (input.Title?.Trim() is not { Length: > 0 and <= 200 } || input.Description is { Length: > 4000 })
        {
            return Error.Validation("playlists.title", "A title of up to 200 characters, and a description of up to 4,000.");
        }

        if (input.AgeMin is < 5 or > 25 || input.AgeMax is < 5 or > 25 || input.AgeMin > input.AgeMax
            || input.EstimatedHours is < 0 or > 1000 || input.RequiredStepCount is < 1 or > MaxSteps)
        {
            return Error.Validation("playlists.range", "Ages from 5 to 25, up to 1,000 hours, and between 1 and 50 steps to complete.");
        }

        if (input.CompletionBadgeClassId is { } badge && await OwnBadgeAsync(org, badge, ct).ConfigureAwait(false) is null)
        {
            return Error.Validation("playlists.badge", "A published badge of your organisation, or none.");
        }

        return input.RequiresBadgeClassId is { } needed
               && !await db.BadgeClasses.AnyAsync(c => c.Id == needed && c.Status != Badges.Badges.Draft, ct).ConfigureAwait(false)
            ? Error.Validation("playlists.required_badge", "A published badge of this county, or none.")
            : null;
    }

    private Task<BadgeClassRecord?> OwnBadgeAsync(OrganisationId org, Guid badge, CancellationToken ct) =>
        (from c in db.BadgeClasses
         join i in db.Issuers on c.IssuerId equals i.Id
         where c.Id == badge && i.OrgId == org.Value && c.Status == Badges.Badges.Published
         select c).FirstOrDefaultAsync(ct);

    private async Task<bool> IsPublishedListingAsync(Guid listingId, CancellationToken ct) =>
        await listings.GetForReviewAsync(tenant.TenantId, listingId, "en", ct).ConfigureAwait(false) is { } listing
        && await listings.GetPublishedAsync(tenant.TenantId, listing.Slug, "en", ct).ConfigureAwait(false) is not null;

    private Task<PlaylistRecord?> OwnAsync(OrganisationId org, Guid id, CancellationToken ct) =>
        db.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.OrgId == org.Value, ct);

    private async Task<Result<PlaylistRecord>> DraftAsync(OrganisationId org, Guid id, CancellationToken ct) =>
        await OwnAsync(org, id, ct).ConfigureAwait(false) is not { } playlist
            ? Result<PlaylistRecord>.Failure(NotFound())
            : playlist.Status != Draft
                ? Result<PlaylistRecord>.Failure(Error.Conflict("playlists.not_draft", "Only a draft playlist changes. A published one never does."))
                : Result<PlaylistRecord>.Success(playlist);

    private static string Slugged(string title)
    {
        string slug = NotSlug().Replace(title.Trim().ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "playlist" : slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    private static Dictionary<string, string> What(string what) => new(StringComparer.Ordinal) { ["what"] = what };

    private Task AuditAsync(string action, OrganisationId org, IReadOnlyCollection<Guid> subjects, CancellationToken ct, string? detail = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = action == Defined ? "playlist" : "playlist_enrollment",
            SubjectIds = subjects,
            ActorOrgId = org,
            Detail = detail is null ? new Dictionary<string, string>() : What(detail),
        }, ct);

    private static Error NotFound() => Error.NotFound("playlists.not_found", "There's no playlist like that.");

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}
