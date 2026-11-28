using BrightTime.Abstractions.Screening;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Modules.Community.Contracts;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Persistence;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Groups;

/// <summary>
/// A challenge as a space's list shows it. <see cref="MyState"/> is the viewer's own work's state,
/// if they've started any; <see cref="Sent"/> and <see cref="Waiting"/> count what's been sent and
/// what's still waiting for feedback, for staff (zero for young people).
/// </summary>
[YouthData]
public sealed record ChallengeSummary(Guid Id, string Title, string Status, DateTimeOffset? DueAt, DateTimeOffset SetAt, string? MyState, int Sent, int Waiting);

/// <summary>A criterion's score in a piece of feedback (D125): what it scored, out of the rubric's scale.</summary>
public sealed record ScoreView(Guid CriterionId, string Label, int Score, int ScaleMax);

/// <summary>
/// Feedback on a piece of work. <see cref="From"/> is the member of staff's email for their
/// colleagues, and null for young people, who see the organisation's staff, never a name. Scored
/// against the challenge's rubric (D125), it has a score for every criterion, and
/// <see cref="Percent"/> is their weighted total out of the most each could score.
/// </summary>
[YouthData]
public sealed record FeedbackView(Guid Id, string? From, string Comment, string? Outcome, DateTimeOffset At, IReadOnlyList<ScoreView> Scores, int? Percent);

/// <summary>
/// A young person's work for a challenge. <see cref="AuthorName"/> is the activity roster's name,
/// for staff; <see cref="RefusedFor"/> is why screening kept it from being sent the last time its
/// author tried, so they can take that out.
/// </summary>
[YouthData]
public sealed record SubmissionView(
    Guid Id,
    string? AuthorName,
    bool Mine,
    string Body,
    string State,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? RefusedFor,
    IReadOnlyList<FeedbackView> Feedback);

/// <summary>
/// A challenge with the work for it: a young person's own (<see cref="Mine"/>), or, for staff,
/// everything sent (<see cref="Submissions"/>). Drafts are their author's alone.
/// </summary>
[YouthData]
public sealed record ChallengeView(
    Guid Id,
    Guid SpaceId,
    string SpaceName,
    string OrgName,
    string Title,
    string Brief,
    string Status,
    DateTimeOffset? DueAt,
    DateTimeOffset SetAt,
    bool CanSubmit,
    bool CanReview,
    SubmissionView? Mine,
    IReadOnlyList<SubmissionView> Submissions,
    RubricView? Rubric);

/// <summary>
/// Challenges (M5, D122): the space's staff set one, with a brief and a due date if they like; its
/// young people write their work, save it, and send it; staff give feedback, accepting it or
/// sending it back for another go. Work is between its author and the space's staff, and the
/// organisation, which reads everything in its spaces (T1): other young people don't see it.
/// </summary>
/// <remarks>
/// <para>
/// What a young person sends is screened for personal information (D117), as a project is (D118):
/// work that doesn't pass isn't sent, stays theirs to change, and they're told why. Each check is a
/// <c>screening_result</c> on the submission. What staff write where a young person reads (a brief,
/// feedback) is screened for an attempt to take them off the platform (D53), and refused, with why,
/// if it doesn't pass. A muted adult (D119) writes neither.
/// </para>
/// <para>
/// One piece of work per young person and challenge, saved as often as they like while it's a draft
/// or sent back; once sent, it waits for feedback. A closed challenge takes no more work, and staff
/// still give feedback on what was sent. An archived space takes nothing new.
/// </para>
/// </remarks>
public sealed class Challenges(
    CommunityDbContext db,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IDirectoryQueries directory,
    IScreeningService screener,
    Spaces spaces)
{
    public const string Read = "challenges.read";
    public const string Set = "challenges.set";
    public const string Closed = "challenges.closed";
    public const string Saved = "challenges.saved";
    public const string Sent = "challenges.submitted";
    public const string FeedbackGiven = "challenges.feedback_given";

    public const int MaxTitle = 120;
    public const int MaxBrief = 4000;
    public const int MaxBody = 8000;
    public const int MaxComment = 2000;

    /// <summary>Sets a challenge in a space, for one of its staff, with one of the organisation's rubrics if they like (D125).</summary>
    public Task<Result<Guid>> SetAsync(UserId staff, Guid spaceId, string? title, string? brief, DateTimeOffset? dueAt, Guid? rubricId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, _) = await MembershipAsync(staff, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null)
            {
                return Result<Guid>.Failure(NotFound());
            }

            if (Staffing(space, me) is { } refused)
            {
                return Result<Guid>.Failure(refused);
            }

            if (title?.Trim() is not { Length: > 0 and <= MaxTitle } named)
            {
                return Result<Guid>.Failure(Error.Validation("challenges.title", $"A title of 1 to {MaxTitle} characters."));
            }

            if (brief?.Trim() is not { Length: > 0 and <= MaxBrief } about)
            {
                return Result<Guid>.Failure(Error.Validation("challenges.brief", $"A brief of 1 to {MaxBrief} characters."));
            }

            DateTimeOffset now = clock.StoredNow();
            if (dueAt is { } due && due <= now)
            {
                return Result<Guid>.Failure(Error.Validation("challenges.due", "A due date still to come, or none."));
            }

            if (rubricId is { } rubric
                && !await db.Rubrics.AnyAsync(r => r.Id == rubric && r.OrgId == space.OrgId && r.ArchivedAt == null, ct).ConfigureAwait(false))
            {
                return Result<Guid>.Failure(Error.Validation("challenges.rubric", "Choose one of your organisation's rubrics, or none."));
            }

            if (await AdultWritingAsync(space, me, $"{named}\n{about}", now, ct).ConfigureAwait(false) is { } kept)
            {
                return Result<Guid>.Failure(kept);
            }

            ChallengeRecord challenge = new()
            {
                Id = UuidV7.NewId(),
                TenantId = space.TenantId,
                SpaceId = space.Id,
                Title = named,
                BriefMd = about,
                DueAt = dueAt,
                RubricId = rubricId,
                Status = ChallengeStatuses.Open,
                CreatedBy = staff.Value,
                CreatedAt = now,
            };
            db.Challenges.Add(challenge);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Set, "challenge", [challenge.Id], ct, new OrganisationId(space.OrgId)).ConfigureAwait(false);
            return Result<Guid>.Success(challenge.Id);
        }, cancellationToken);

    /// <summary>Closes a challenge: it takes no more work; staff still give feedback.</summary>
    public Task<Result> CloseAsync(UserId staff, Guid spaceId, Guid challengeId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, _) = await MembershipAsync(staff, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null)
            {
                return Result.Failure(NotFound());
            }

            if (Staffing(space, me) is { } refused)
            {
                return Result.Failure(refused);
            }

            DateTimeOffset now = clock.StoredNow();
            int changed = await db.Challenges.Where(c => c.Id == challengeId && c.SpaceId == spaceId && c.Status == ChallengeStatuses.Open)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, ChallengeStatuses.Closed).SetProperty(c => c.ClosedAt, now), ct).ConfigureAwait(false);
            if (changed == 0)
            {
                return Result.Failure(Error.Conflict("challenges.closed", "This challenge is closed. It takes no more work."));
            }

            await AuditAsync(Closed, "challenge", [challengeId], ct, new OrganisationId(space.OrgId)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>A space's challenges, newest first, for one of its members.</summary>
    public Task<Result<IReadOnlyList<ChallengeSummary>>> ListAsync(UserId user, Guid spaceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, _) = await MembershipAsync(user, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null)
            {
                return Result<IReadOnlyList<ChallengeSummary>>.Failure(NotFound());
            }

            IReadOnlyList<ChallengeSummary> list = await SummariesAsync(space, user, me.MemberRole != SpaceRoles.Youth, ct).ConfigureAwait(false);
            await AuditAsync(Read, "challenge", [.. list.Select(c => c.Id)], ct).ConfigureAwait(false);
            return Result<IReadOnlyList<ChallengeSummary>>.Success(list);
        }, cancellationToken);

    /// <summary>A space's challenges, for the organisation's staff, member or not. The caller has checked <c>spaces.view</c>.</summary>
    public Task<Result<IReadOnlyList<ChallengeSummary>>> ListForOrganisationAsync(OrganisationId org, UserId staff, Guid spaceId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space)
            {
                return Result<IReadOnlyList<ChallengeSummary>>.Failure(NotFound());
            }

            IReadOnlyList<ChallengeSummary> list = await SummariesAsync(space, staff, forStaff: true, ct).ConfigureAwait(false);
            await AuditAsync(Read, "challenge", [.. list.Select(c => c.Id)], ct, org).ConfigureAwait(false);
            return Result<IReadOnlyList<ChallengeSummary>>.Success(list);
        }, cancellationToken);

    /// <summary>A challenge, for one of its space's members: a young person's own work, or, for staff, everything sent.</summary>
    public Task<Result<ChallengeView>> GetAsync(UserId user, Guid spaceId, Guid challengeId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, IReadOnlyDictionary<Guid, string?> names) = await MembershipAsync(user, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null
                || await db.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == challengeId && c.SpaceId == spaceId, ct).ConfigureAwait(false) is not { } challenge)
            {
                return Result<ChallengeView>.Failure(NotFound());
            }

            bool staff = me.MemberRole != SpaceRoles.Youth;
            ChallengeView view = await ViewAsync(space, challenge, user, names, forStaff: staff, canReview: staff, ct).ConfigureAwait(false);
            await AuditAsync(Read, "challenge", [challenge.Id], ct, staff ? new OrganisationId(space.OrgId) : null).ConfigureAwait(false);
            return Result<ChallengeView>.Success(view);
        }, cancellationToken);

    /// <summary>A challenge with everything sent for it, for the organisation's staff; only the space's staff give feedback.</summary>
    public Task<Result<ChallengeView>> GetForOrganisationAsync(OrganisationId org, UserId staff, Guid spaceId, Guid challengeId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId && s.OrgId == org.Value, ct).ConfigureAwait(false) is not { } space
                || await db.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == challengeId && c.SpaceId == spaceId, ct).ConfigureAwait(false) is not { } challenge)
            {
                return Result<ChallengeView>.Failure(NotFound());
            }

            IReadOnlyDictionary<Guid, string?> names = await spaces.ReconcileAsync(space, ct).ConfigureAwait(false);
            bool member = await db.SpaceMembers.AsNoTracking()
                .AnyAsync(m => m.SpaceId == space.Id && m.UserId == staff.Value && m.LeftAt == null && m.MemberRole != SpaceRoles.Youth, ct).ConfigureAwait(false);
            ChallengeView view = await ViewAsync(space, challenge, staff, names, forStaff: true, canReview: member && space.Status == SpaceStatuses.Active, ct).ConfigureAwait(false);
            await AuditAsync(Read, "challenge", [challenge.Id], ct, org).ConfigureAwait(false);
            return Result<ChallengeView>.Success(view);
        }, cancellationToken);

    /// <summary>
    /// A young person saves their work, and sends it when <paramref name="send"/>: screened first,
    /// and kept back, with why, if it shares personal information.
    /// </summary>
    public Task<Result<SubmissionView>> SaveAsync(UserId youth, Guid spaceId, Guid challengeId, string? body, bool send, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, _) = await MembershipAsync(youth, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null
                || await db.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == challengeId && c.SpaceId == spaceId, ct).ConfigureAwait(false) is not { } challenge)
            {
                return Result<SubmissionView>.Failure(NotFound());
            }

            if (me.MemberRole != SpaceRoles.Youth)
            {
                return Result<SubmissionView>.Failure(Error.Forbidden("challenges.youth_only", "Only the space's young people send work for a challenge."));
            }

            if (space.Status != SpaceStatuses.Active || challenge.Status != ChallengeStatuses.Open)
            {
                return Result<SubmissionView>.Failure(Error.Conflict("challenges.closed", "This challenge is closed. It takes no more work."));
            }

            if (body?.Trim() is not { Length: > 0 and <= MaxBody } text)
            {
                return Result<SubmissionView>.Failure(Error.Validation("challenges.body", $"Your work, in 1 to {MaxBody} characters."));
            }

            // Two saves at once both find no work yet: the second gives way to the first's row, by
            // ux_submission_once, and saves into it.
            DateTimeOffset now = clock.StoredNow();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                insert into community.submission (id, tenant_id, challenge_id, author_user_id, state, created_at, updated_at)
                values ({UuidV7.NewId()}, {space.TenantId}, {challenge.Id}, {youth.Value}, {SubmissionStates.Draft}, {now}, {now})
                on conflict (challenge_id, author_user_id) do nothing
                """, ct).ConfigureAwait(false);
            SubmissionRecord work = await db.Submissions.FirstAsync(s => s.ChallengeId == challenge.Id && s.AuthorUserId == youth.Value, ct).ConfigureAwait(false);
            if (!SubmissionStates.Editable(work.State))
            {
                return Result<SubmissionView>.Failure(Error.Conflict("challenges.sent", "You've sent this. It waits for feedback, and you can change it if it's sent back."));
            }

            work.Body = text;
            work.UpdatedAt = now;
            IReadOnlyList<string>? refused = null;
            if (send)
            {
                ScreenVerdict verdict = (await ContentScreening.ScreenAsync(screener, text, MessageScreen.YouthPii, audienceIncludesMinor: true, "submission", ct).ConfigureAwait(false))!;
                db.ScreeningResults.Add(verdict.Record(space.TenantId, "submission", work.Id, now));
                work.ScreenResult = verdict.ScreenResult;
                work.ScreenedAt = now;
                if (verdict.Passed)
                {
                    work.State = SubmissionStates.Submitted;
                    work.SubmittedAt = now;
                }
                else
                {
                    refused = verdict.Reasons;
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(send && refused is null ? Sent : Saved, "submission", [work.Id], ct).ConfigureAwait(false);
            IReadOnlyList<FeedbackView> feedback = await FeedbackOnAsync(work.Id, forStaff: false, ct).ConfigureAwait(false);
            return Result<SubmissionView>.Success(new SubmissionView(work.Id, null, true, text, work.State, work.SubmittedAt, work.UpdatedAt, refused, feedback));
        }, cancellationToken);

    /// <summary>
    /// One of the space's staff gives feedback on work that was sent: a comment, and, if they like, a
    /// decision (accepted, or sent back for another go), and a score for every one of the challenge's
    /// rubric's criteria (D125), or none.
    /// </summary>
    public Task<Result> GiveFeedbackAsync(
        UserId staff, Guid spaceId, Guid challengeId, Guid submissionId, string? comment, string? outcome, IReadOnlyDictionary<Guid, int>? scores,
        CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            (SpaceRecord? space, SpaceMemberRecord? me, _) = await MembershipAsync(staff, spaceId, ct).ConfigureAwait(false);
            if (space is null || me is null
                || await db.Challenges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == challengeId && c.SpaceId == spaceId, ct).ConfigureAwait(false) is not { } challenge)
            {
                return Result.Failure(NotFound());
            }

            if (Staffing(space, me) is { } refused)
            {
                return Result.Failure(refused);
            }

            if (await db.Submissions.FirstOrDefaultAsync(s => s.Id == submissionId && s.ChallengeId == challengeId && s.State != SubmissionStates.Draft, ct).ConfigureAwait(false) is not { } work)
            {
                return Result.Failure(Error.NotFound("challenges.submission_not_found", "There's no work like that sent for this challenge."));
            }

            if (comment?.Trim() is not { Length: > 0 and <= MaxComment } said)
            {
                return Result.Failure(Error.Validation("challenges.comment", $"Feedback of 1 to {MaxComment} characters."));
            }

            string? decided = outcome is { Length: > 0 } o ? o : null;
            if (decided is not (null or FeedbackOutcomes.Accepted or FeedbackOutcomes.NeedsRework))
            {
                return Result.Failure(Error.Validation("challenges.outcome", "Accept it, send it back, or just comment."));
            }

            if (work.State == SubmissionStates.NeedsRework && decided is not null)
            {
                return Result.Failure(Error.Conflict("challenges.with_author", "It's been sent back, and waits for its author to send it again."));
            }

            // All or none: a partial score would read as a total it isn't.
            List<(Guid Criterion, short Score)> scored = [];
            if (scores is { Count: > 0 })
            {
                RubricRecord? rubric = challenge.RubricId is { } rid
                    ? await db.Rubrics.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct).ConfigureAwait(false)
                    : null;
                if (rubric is null)
                {
                    return Result.Failure(Error.Validation("challenges.no_rubric", "This challenge has no rubric to score against."));
                }

                List<Guid> criteria = await db.RubricCriteria.AsNoTracking().Where(c => c.RubricId == rubric.Id).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
                if (scores.Count != criteria.Count || criteria.Any(c => !scores.TryGetValue(c, out int s) || s < 0 || s > rubric.ScaleMax))
                {
                    return Result.Failure(Error.Validation("challenges.scores", $"Score every criterion from 0 to {rubric.ScaleMax}, or none.",
                        arguments: new Dictionary<string, object> { ["max"] = (int)rubric.ScaleMax }));
                }

                scored = [.. criteria.Select(c => (c, (short)scores[c]))];
            }

            DateTimeOffset now = clock.StoredNow();
            if (await AdultWritingAsync(space, me, said, now, ct, toMinor: await identity.IsMinorAsync(new UserId(work.AuthorUserId), ct).ConfigureAwait(false)).ConfigureAwait(false) is { } kept)
            {
                return Result.Failure(kept);
            }

            Guid feedbackId = UuidV7.NewId();
            db.Feedback.Add(new FeedbackRecord
            {
                Id = feedbackId,
                TenantId = space.TenantId,
                SubmissionId = work.Id,
                MentorUserId = staff.Value,
                Comment = said,
                Outcome = decided,
                CreatedAt = now,
            });
            db.FeedbackScores.AddRange(scored.Select(s => new FeedbackScoreRecord { FeedbackId = feedbackId, CriterionId = s.Criterion, Score = s.Score }));
            work.State = decided switch
            {
                FeedbackOutcomes.Accepted => SubmissionStates.Accepted,
                FeedbackOutcomes.NeedsRework => SubmissionStates.NeedsRework,
                _ => work.State == SubmissionStates.Submitted ? SubmissionStates.FeedbackGiven : work.State,
            };
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(FeedbackGiven, "submission", [work.Id], ct, new OrganisationId(space.OrgId)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>The space, brought up to date, the person's membership of it, and the activity's young people's names.</summary>
    private async Task<(SpaceRecord? Space, SpaceMemberRecord? Me, IReadOnlyDictionary<Guid, string?> Names)> MembershipAsync(UserId user, Guid spaceId, CancellationToken ct)
    {
        if (await db.Spaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct).ConfigureAwait(false) is not { } space)
        {
            return (null, null, new Dictionary<Guid, string?>());
        }

        IReadOnlyDictionary<Guid, string?> names = await spaces.ReconcileAsync(space, ct).ConfigureAwait(false);
        SpaceMemberRecord? me = await db.SpaceMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SpaceId == space.Id && m.UserId == user.Value && m.LeftAt == null, ct).ConfigureAwait(false);
        return (space, me, names);
    }

    /// <summary>Why someone may not set challenges or give feedback here, if they may not.</summary>
    private static Error? Staffing(SpaceRecord space, SpaceMemberRecord me)
    {
        if (me.MemberRole == SpaceRoles.Youth || me.IsMinor)
        {
            return Error.Forbidden("challenges.not_staff", "Only the space's staff set challenges and give feedback.");
        }

        return space.Status == SpaceStatuses.Active ? null : Error.Conflict("spaces.archived", "This space has been archived. It takes nothing new.");
    }

    /// <summary>
    /// What an adult writes where a young person reads: refused if a moderator has muted them, or
    /// if it looks like an attempt to take a young person off the platform (D53), with why.
    /// </summary>
    private async Task<Error?> AdultWritingAsync(SpaceRecord space, SpaceMemberRecord me, string text, DateTimeOffset now, CancellationToken ct, bool? toMinor = null)
    {
        bool minors = toMinor ?? await db.SpaceMembers.AnyAsync(m => m.SpaceId == space.Id && m.LeftAt == null && m.IsMinor, ct).ConfigureAwait(false);
        if (!minors)
        {
            return null;
        }

        if (await Moderation.Restrictions.IsMutedAsync(db, me.UserId, now, ct).ConfigureAwait(false))
        {
            return Error.Forbidden("challenges.muted", "A moderator has paused your writing to young people for now.");
        }

        ScreenVerdict? verdict = await ContentScreening.ScreenAsync(screener, text, MessageScreen.ContactExchange, audienceIncludesMinor: true, "challenge", ct).ConfigureAwait(false);
        return verdict is null || verdict.Passed
            ? null
            : Error.Validation("challenges.screened", $"It looks like a way to talk away from BrightTime ({string.Join(", ", verdict.Reasons)}). Take that out and try again.");
    }

    private async Task<IReadOnlyList<ChallengeSummary>> SummariesAsync(SpaceRecord space, UserId viewer, bool forStaff, CancellationToken ct)
    {
        List<ChallengeRecord> challenges = await db.Challenges.AsNoTracking().Where(c => c.SpaceId == space.Id)
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        List<Guid> ids = [.. challenges.Select(c => c.Id)];
        var work = await db.Submissions.AsNoTracking().Where(s => ids.Contains(s.ChallengeId))
            .Select(s => new { s.ChallengeId, s.AuthorUserId, s.State }).ToListAsync(ct).ConfigureAwait(false);
        return [.. challenges.Select(c =>
        {
            var mine = work.FirstOrDefault(w => w.ChallengeId == c.Id && w.AuthorUserId == viewer.Value);
            int sent = forStaff ? work.Count(w => w.ChallengeId == c.Id && w.State != SubmissionStates.Draft) : 0;
            int waiting = forStaff ? work.Count(w => w.ChallengeId == c.Id && w.State == SubmissionStates.Submitted) : 0;
            return new ChallengeSummary(c.Id, c.Title, c.Status, c.DueAt, c.CreatedAt, mine?.State, sent, waiting);
        })];
    }

    private async Task<ChallengeView> ViewAsync(SpaceRecord space, ChallengeRecord challenge, UserId viewer, IReadOnlyDictionary<Guid, string?> names, bool forStaff, bool canReview, CancellationToken ct)
    {
        string org = await directory.GetOrganisationNameAsync(new OrganisationId(space.OrgId), ct).ConfigureAwait(false) ?? "";
        SubmissionView? mine = null;
        List<SubmissionView> sent = [];
        if (forStaff)
        {
            foreach (SubmissionRecord s in await db.Submissions.AsNoTracking()
                .Where(s => s.ChallengeId == challenge.Id && s.State != SubmissionStates.Draft)
                .OrderBy(s => s.SubmittedAt).ThenBy(s => s.Id).ToListAsync(ct).ConfigureAwait(false))
            {
                sent.Add(new SubmissionView(s.Id, names.GetValueOrDefault(s.AuthorUserId), false, s.Body ?? "", s.State, s.SubmittedAt, s.UpdatedAt, null,
                    await FeedbackOnAsync(s.Id, forStaff: true, ct).ConfigureAwait(false)));
            }
        }
        else if (await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.ChallengeId == challenge.Id && s.AuthorUserId == viewer.Value, ct).ConfigureAwait(false) is { } own)
        {
            // Screening's reasons stand until the work is changed again.
            IReadOnlyList<string>? refused = SubmissionStates.Editable(own.State) && own.ScreenedAt == own.UpdatedAt
                && Moderation.HeldMessages.Reasons(own.ScreenResult) is { Count: > 0 } reasons ? reasons : null;
            mine = new SubmissionView(own.Id, null, true, own.Body ?? "", own.State, own.SubmittedAt, own.UpdatedAt, refused,
                await FeedbackOnAsync(own.Id, forStaff: false, ct).ConfigureAwait(false));
        }

        bool youthMember = !forStaff && await db.SpaceMembers.AsNoTracking()
            .AnyAsync(m => m.SpaceId == space.Id && m.UserId == viewer.Value && m.LeftAt == null && m.MemberRole == SpaceRoles.Youth, ct).ConfigureAwait(false);
        bool canSubmit = youthMember && space.Status == SpaceStatuses.Active && challenge.Status == ChallengeStatuses.Open
            && (mine is null || SubmissionStates.Editable(mine.State));
        RubricView? rubric = challenge.RubricId is { } rid
            && await db.Rubrics.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct).ConfigureAwait(false) is { } found
                ? (await Rubrics.ViewsAsync(db, [found], ct).ConfigureAwait(false))[0]
                : null;
        return new ChallengeView(challenge.Id, space.Id, space.Name, org, challenge.Title, challenge.BriefMd, challenge.Status, challenge.DueAt, challenge.CreatedAt,
            canSubmit, canReview, mine, sent, rubric);
    }

    private async Task<IReadOnlyList<FeedbackView>> FeedbackOnAsync(Guid submission, bool forStaff, CancellationToken ct)
    {
        List<FeedbackRecord> feedback = await db.Feedback.AsNoTracking().Where(f => f.SubmissionId == submission)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync(ct).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, string> emails = forStaff && feedback.Count > 0
            ? await identity.GetAdultEmailsAsync([.. feedback.Select(f => f.MentorUserId).Distinct()], ct).ConfigureAwait(false)
            : new Dictionary<Guid, string>();
        List<Guid> ids = [.. feedback.Select(f => f.Id)];
        var scores = await (from s in db.FeedbackScores.AsNoTracking()
                            join c in db.RubricCriteria on s.CriterionId equals c.Id
                            join r in db.Rubrics on c.RubricId equals r.Id
                            where ids.Contains(s.FeedbackId)
                            orderby c.SortOrder, c.Code
                            select new { s.FeedbackId, s.CriterionId, c.Label, c.Weight, s.Score, r.ScaleMax })
            .ToListAsync(ct).ConfigureAwait(false);
        ILookup<Guid, ScoreView> byFeedback = scores.ToLookup(s => s.FeedbackId, s => new ScoreView(s.CriterionId, s.Label, s.Score, s.ScaleMax));
        Dictionary<Guid, int> percents = scores.GroupBy(s => s.FeedbackId).ToDictionary(
            g => g.Key,
            g => (int)Math.Round(100m * g.Sum(s => s.Score * s.Weight) / g.Sum(s => s.ScaleMax * s.Weight), MidpointRounding.AwayFromZero));
        return [.. feedback.Select(f => new FeedbackView(f.Id, forStaff ? emails.GetValueOrDefault(f.MentorUserId) : null, f.Comment ?? "", f.Outcome, f.CreatedAt,
            [.. byFeedback[f.Id]], percents.TryGetValue(f.Id, out int percent) ? percent : null))];
    }

    private Task AuditAsync(string action, string kind, IReadOnlyCollection<Guid> subjects, CancellationToken ct, OrganisationId? org = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = kind,
            SubjectIds = subjects,
            ActorOrgId = org,
        }, ct);

    private static Error NotFound() => Error.NotFound("challenges.not_found", "There's no challenge like that in a space you're in.");
}
