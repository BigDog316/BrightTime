using System.Globalization;
using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Workforce.Contracts;
using BrightTime.Modules.Workforce.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Workforce.Training;

/// <summary>A unit as the county's organiser writes it (D130). Markdown body, an https link, or both.</summary>
public sealed record CourseUnitInput(string? Title, string? Body, string? Link, int? Minutes);

/// <summary>
/// A course as the county's organiser writes it (D130). <see cref="Units"/> replaces the units, in
/// order, while the course is a draft; null leaves them as they are, which is all a published
/// course allows.
/// </summary>
public sealed record CourseInput(
    string? Slug,
    string? Title,
    string? Summary,
    string? ProviderName,
    string? Delivery,
    decimal? Hours,
    int? ValidityMonths,
    IReadOnlyList<CourseUnitInput>? Units);

/// <summary>One unit of a course, in order.</summary>
public sealed record CourseUnitView(Guid Id, int Order, string Title, string? Body, string? Link, int? Minutes);

/// <summary>A course with its units (D130).</summary>
public sealed record CourseView(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string? ProviderName,
    string Delivery,
    decimal? Hours,
    int? ValidityMonths,
    string Status,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<CourseUnitView> Units);

/// <summary>A course in the organiser's list: its state, its units, how many have completed it, and whom it's required of.</summary>
public sealed record CourseRow(Guid Id, string Slug, string Title, string Status, int Units, int Completions, IReadOnlyList<string> RequiredOf);

/// <summary>A course the county requires of a role (D130).</summary>
public sealed record TrainingRequirementView(string AppliesTo, Guid CourseId, string CourseTitle, bool IsBlocking, int GraceDays);

/// <summary>A course in the catalogue as one person sees it: where they stand with it, and whether their roles require it.</summary>
public sealed record CatalogueEntry(
    Guid Id,
    string Slug,
    string Title,
    string? Summary,
    string? ProviderName,
    string Delivery,
    decimal? Hours,
    int Units,
    int Done,
    string State,
    DateOnly? ExpiresOn,
    bool Required);

/// <summary>A course as one person takes it: the units they've done, and when it was completed and counts until.</summary>
public sealed record TrainingCourseView(
    CourseView Course,
    string State,
    IReadOnlyList<Guid> Done,
    DateTimeOffset? CompletedAt,
    DateOnly? ExpiresOn,
    bool Required);

/// <summary>
/// One course someone's roles require (D130): where they stand with it, and whether it counts now:
/// completed and not past its date, or still inside the county's grace (from when they took the
/// role, or from when their completion ran out). <see cref="DueBy"/> is the grace's last day.
/// </summary>
public sealed record RequiredCourse(
    Guid CourseId,
    string Slug,
    string Title,
    IReadOnlyList<string> AppliesTo,
    bool IsBlocking,
    string State,
    DateOnly? ExpiresOn,
    DateOnly? DueBy,
    bool Current);

/// <summary>Someone's training against what the county requires of their roles; <see cref="Current"/> when every blocking course counts.</summary>
public sealed record TrainingStatus(IReadOnlyList<RequiredCourse> Required, bool Current);

/// <summary>
/// The county's training catalogue (M6, D130). The county's organisers write courses, unit by unit,
/// and publish them; a published course's units are fixed, since people's progress is kept against
/// them. Organisers require courses of the roles people hold at organisations (mentors, staff,
/// organisations' admins), each with a grace in days.
/// </summary>
/// <remarks>
/// <para>
/// Adults who hold a role at an organisation take courses in BrightTime: they read each unit and mark
/// it done; the last one completes the course, which then counts for the course's validity in months,
/// or for good. Marking a unit of a course whose completion has run out starts it again. Young people
/// don't take staff training here: a minor is refused.
/// </para>
/// <para>
/// Writing courses and requirements needs <c>courses.author</c>, which the caller checks. Taking
/// them needs only an active membership somewhere, which this checks. Nothing here is a young
/// person's record. Blocking requirements are carried, for the gig board to refuse unqualified
/// mentors later; nothing refuses anyone yet. A badge or certificate for finishing waits on Learning.
/// </para>
/// </remarks>
public sealed partial class Training(
    WorkforceDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IDirectoryQueries directory,
    IIdentityQueries identity)
{
    public const string CourseSaved = "workforce.course.saved";
    public const string CoursePublished = "workforce.course.published";
    public const string CourseRetired = "workforce.course.retired";
    public const string RequirementSet = "workforce.training.requirement_set";
    public const string RequirementRemoved = "workforce.training.requirement_removed";
    public const string UnitDone = "workforce.training.unit_done";
    public const string CourseCompleted = "workforce.training.completed";

    public const int MaxTitle = 120;
    public const int MaxSummary = 1000;
    public const int MaxProvider = 120;
    public const int MaxBody = 8000;
    public const int MaxUnits = 10;
    public const int MaxGraceDays = 365;

    // ---- the organiser's ---------------------------------------------------------------------

    /// <summary>Every course, drafts and retired ones too, newest first.</summary>
    public Task<IReadOnlyList<CourseRow>> ListAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<CourseRow>>(async ct =>
        {
            List<CourseRecord> courses = await db.Courses.AsNoTracking().OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> ids = [.. courses.Select(c => c.Id)];
            Dictionary<Guid, int> units = await db.CourseUnits.AsNoTracking().Where(u => ids.Contains(u.CourseId))
                .GroupBy(u => u.CourseId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
            Dictionary<Guid, int> completions = await db.CourseEnrollments.AsNoTracking().Where(e => ids.Contains(e.CourseId) && e.CompletedAt != null)
                .GroupBy(e => e.CourseId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
            List<TrainingRequirementRecord> requirements = await db.TrainingRequirements.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
            return [.. courses.Select(c => new CourseRow(
                c.Id, c.Slug, c.Title, c.Status, units.GetValueOrDefault(c.Id), completions.GetValueOrDefault(c.Id),
                [.. requirements.Where(r => r.CourseId == c.Id).Select(r => r.AppliesTo).Order(StringComparer.Ordinal)]))];
        }, cancellationToken);

    /// <summary>One course with its units, in any state.</summary>
    public Task<CourseView?> GetAsync(Guid courseId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => ViewAsync(courseId, ct), cancellationToken);

    /// <summary>
    /// Writes a new course as a draft, or changes one. A draft's slug and units can change; a
    /// published or retired course keeps both, since people's progress and links point at them.
    /// </summary>
    public Task<Result<Guid>> SaveAsync(Guid? courseId, CourseInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            string? title = Optional(input.Title);
            string? summary = Optional(input.Summary);
            string? provider = Optional(input.ProviderName);
            if (title is null or { Length: > MaxTitle } || summary is { Length: > MaxSummary } || provider is { Length: > MaxProvider })
            {
                return Result<Guid>.Failure(Error.Validation("training.text",
                    $"A title of up to {MaxTitle} characters, a summary of up to {MaxSummary}, and who provides it in up to {MaxProvider}."));
            }

            string delivery = Optional(input.Delivery) ?? CourseDeliveries.SelfPaced;
            if (!CourseDeliveries.All.Contains(delivery, StringComparer.Ordinal))
            {
                return Result<Guid>.Failure(Error.Validation("training.delivery", "Choose how the course is delivered."));
            }

            if (input.Hours is < 0 or > 999 || input.ValidityMonths is < 1 or > 120)
            {
                return Result<Guid>.Failure(Error.Validation("training.numbers", "Up to 999 hours, and a completion that counts for 1 to 120 months, or for good."));
            }

            CourseRecord? course = null;
            if (courseId is { } id)
            {
                course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
                if (course is null)
                {
                    return Result<Guid>.Failure(NoCourse());
                }
            }

            bool draft = course is null || course.Status == CourseStatuses.Draft;
            string slug = Optional(input.Slug)?.ToLowerInvariant() ?? course?.Slug ?? "";
            if (!draft && (input.Units is not null || !string.Equals(slug, course!.Slug, StringComparison.OrdinalIgnoreCase)))
            {
                return Result<Guid>.Failure(Error.Conflict("training.fixed", "A published course's address and units stay as they are; retire it and write a new one to change them."));
            }

            if (!SlugOk(slug))
            {
                return Result<Guid>.Failure(Error.Validation("training.slug", "Give the course an address of 3 to 60 lower-case letters, digits and hyphens."));
            }

            if (await db.Courses.AnyAsync(c => c.Slug == slug && c.Id != (course == null ? Guid.Empty : course.Id), ct).ConfigureAwait(false))
            {
                return Result<Guid>.Failure(Error.Conflict("training.slug_taken", "Another course has that address."));
            }

            List<CourseUnitInput>? units = input.Units is null ? null : [.. input.Units.Where(u => !IsBlank(u))];
            if (units is not null)
            {
                Error? unitProblem = CheckUnits(units);
                if (unitProblem is not null)
                {
                    return Result<Guid>.Failure(unitProblem);
                }
            }

            DateTimeOffset now = clock.StoredNow();
            if (course is null)
            {
                course = new CourseRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    Slug = slug,
                    Title = title,
                    Delivery = delivery,
                    Status = CourseStatuses.Draft,
                    CreatedAt = now,
                };
                db.Courses.Add(course);
            }

            course.Slug = slug;
            course.Title = title;
            course.Summary = summary;
            course.ProviderName = provider;
            course.Delivery = delivery;
            course.Hours = input.Hours;
            course.ValidityMonths = (short?)input.ValidityMonths;
            course.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (units is not null)
            {
                // Only a draft's units are replaced, and nobody takes a draft: no progress is lost.
                Guid cid = course.Id;
                await db.CourseUnits.Where(u => u.CourseId == cid).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                db.CourseUnits.AddRange(units.Select((u, i) => new CourseUnitRecord
                {
                    Id = UuidV7.NewId(),
                    CourseId = cid,
                    SortOrder = (short)(i + 1),
                    Title = u.Title!.Trim(),
                    BodyMd = Optional(u.Body),
                    ExternalUrl = Optional(u.Link),
                    Minutes = (short?)u.Minutes,
                }));
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await AuditAsync(CourseSaved, course.Id, ct, ("units", units?.Count.ToString(CultureInfo.InvariantCulture) ?? "kept")).ConfigureAwait(false);
            return Result<Guid>.Success(course.Id);
        }, cancellationToken);

    /// <summary>Puts a draft in the catalogue. It needs at least one unit; its units are fixed from now on.</summary>
    public Task<Result> PublishAsync(Guid courseId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            CourseRecord? course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId, ct).ConfigureAwait(false);
            if (course is null)
            {
                return Result.Failure(NoCourse());
            }

            if (course.Status != CourseStatuses.Draft)
            {
                return Result.Failure(Error.Conflict("training.not_draft", "Only a draft is published."));
            }

            if (!await db.CourseUnits.AnyAsync(u => u.CourseId == courseId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Validation("training.no_units", "Write at least one unit before you publish the course."));
            }

            DateTimeOffset now = clock.StoredNow();
            course.Status = CourseStatuses.Published;
            course.PublishedAt = now;
            course.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(CoursePublished, courseId, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Takes a published course out of the catalogue. Refused while the county requires it of anyone.</summary>
    public Task<Result> RetireAsync(Guid courseId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            CourseRecord? course = await db.Courses.FirstOrDefaultAsync(c => c.Id == courseId, ct).ConfigureAwait(false);
            if (course is null)
            {
                return Result.Failure(NoCourse());
            }

            if (course.Status != CourseStatuses.Published)
            {
                return Result.Failure(Error.Conflict("training.not_published", "Only a published course is retired."));
            }

            if (await db.TrainingRequirements.AnyAsync(r => r.CourseId == courseId, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Conflict("training.required", "The county requires this course of someone; take the requirement away first."));
            }

            course.Status = CourseStatuses.Retired;
            course.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(CourseRetired, courseId, ct).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>What the county requires of whom.</summary>
    public Task<IReadOnlyList<TrainingRequirementView>> RequirementsAsync(CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<TrainingRequirementView>>(async ct =>
            await (from r in db.TrainingRequirements.AsNoTracking()
                   join c in db.Courses.AsNoTracking() on r.CourseId equals c.Id
                   orderby r.AppliesTo, c.Title
                   select new TrainingRequirementView(r.AppliesTo, r.CourseId, c.Title, r.IsBlocking, r.GraceDays))
                .ToListAsync(ct).ConfigureAwait(false), cancellationToken);

    /// <summary>Requires a published course of a role, or changes how: blocking or not, and the grace in days.</summary>
    public Task<Result> SetRequirementAsync(string appliesTo, Guid courseId, bool isBlocking, int graceDays, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (!TrainingRoles.All.Contains(appliesTo, StringComparer.Ordinal))
            {
                return Result.Failure(Error.Validation("training.role", "Choose mentors, staff or organisations' admins."));
            }

            if (graceDays is < 0 or > MaxGraceDays)
            {
                return Result.Failure(Error.Validation("training.grace", $"A grace of 0 to {MaxGraceDays} days."));
            }

            if (!await db.Courses.AnyAsync(c => c.Id == courseId && c.Status == CourseStatuses.Published, ct).ConfigureAwait(false))
            {
                return Result.Failure(Error.Validation("training.requires_published", "Only a published course can be required."));
            }

            TrainingRequirementRecord? requirement = await db.TrainingRequirements
                .FirstOrDefaultAsync(r => r.AppliesTo == appliesTo && r.CourseId == courseId, ct).ConfigureAwait(false);
            if (requirement is null)
            {
                requirement = new TrainingRequirementRecord { TenantId = tenant.TenantId.Value, AppliesTo = appliesTo, CourseId = courseId };
                db.TrainingRequirements.Add(requirement);
            }

            requirement.IsBlocking = isBlocking;
            requirement.GraceDays = (short)graceDays;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(RequirementSet, courseId, ct, ("applies_to", appliesTo), ("blocking", isBlocking ? "true" : "false"),
                ("grace_days", graceDays.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Stops requiring a course of a role. Completions stay on record.</summary>
    public Task<Result> RemoveRequirementAsync(string appliesTo, Guid courseId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            int removed = await db.TrainingRequirements.Where(r => r.AppliesTo == appliesTo && r.CourseId == courseId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (removed == 0)
            {
                return Result.Failure(Error.NotFound("training.no_requirement", "The county doesn't require that course of that role."));
            }

            await AuditAsync(RequirementRemoved, courseId, ct, ("applies_to", appliesTo)).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    // ---- the learner's -----------------------------------------------------------------------

    /// <summary>The published courses, with where this person stands with each and whether their roles require it.</summary>
    public Task<Result<IReadOnlyList<CatalogueEntry>>> CatalogueAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Error? refused = await RefusedAsync(user, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return Result<IReadOnlyList<CatalogueEntry>>.Failure(refused);
            }

            List<CourseRecord> courses = await db.Courses.AsNoTracking().Where(c => c.Status == CourseStatuses.Published)
                .OrderBy(c => c.Title).ThenBy(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
            List<Guid> ids = [.. courses.Select(c => c.Id)];
            Dictionary<Guid, int> units = await db.CourseUnits.AsNoTracking().Where(u => ids.Contains(u.CourseId))
                .GroupBy(u => u.CourseId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
            Dictionary<Guid, CourseEnrollmentRecord> mine = await db.CourseEnrollments.AsNoTracking()
                .Where(e => e.UserId == user.Value && ids.Contains(e.CourseId)).ToDictionaryAsync(e => e.CourseId, ct).ConfigureAwait(false);
            List<Guid> enrolments = [.. mine.Values.Select(e => e.Id)];
            Dictionary<Guid, int> done = await db.UnitProgress.AsNoTracking().Where(p => enrolments.Contains(p.CourseEnrollmentId))
                .GroupBy(p => p.CourseEnrollmentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct).ConfigureAwait(false);
            HashSet<Guid> required = [.. (await RequirementsForAsync(user, ct).ConfigureAwait(false)).Select(r => r.CourseId)];
            DateOnly today = Today;
            return Result<IReadOnlyList<CatalogueEntry>>.Success([.. courses.Select(c =>
            {
                CourseEnrollmentRecord? e = mine.GetValueOrDefault(c.Id);
                return new CatalogueEntry(c.Id, c.Slug, c.Title, c.Summary, c.ProviderName, c.Delivery, c.Hours, units.GetValueOrDefault(c.Id),
                    e is null ? 0 : done.GetValueOrDefault(e.Id), StateOf(e, today), e?.ExpiresOn, required.Contains(c.Id));
            })]);
        }, cancellationToken);

    /// <summary>A published course as this person takes it.</summary>
    public Task<Result<TrainingCourseView>> CourseAsync(UserId user, string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Error? refused = await RefusedAsync(user, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return Result<TrainingCourseView>.Failure(refused);
            }

            Guid? id = await db.Courses.AsNoTracking().Where(c => c.Slug == slug && c.Status == CourseStatuses.Published)
                .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return id is { } courseId
                ? Result<TrainingCourseView>.Success(await TakingAsync(user, courseId, ct).ConfigureAwait(false))
                : Result<TrainingCourseView>.Failure(NoCourse());
        }, cancellationToken);

    /// <summary>
    /// Marks a unit of a published course done, starting the course if need be. The last unit
    /// completes it, until today plus the course's validity; a unit of a course whose completion
    /// has run out starts it again. Marking a unit twice changes nothing.
    /// </summary>
    public Task<Result<TrainingCourseView>> CompleteUnitAsync(UserId user, Guid courseId, Guid unitId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            Error? refused = await RefusedAsync(user, ct).ConfigureAwait(false);
            if (refused is not null)
            {
                return Result<TrainingCourseView>.Failure(refused);
            }

            CourseRecord? course = await db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.Id == courseId && c.Status == CourseStatuses.Published, ct).ConfigureAwait(false);
            if (course is null)
            {
                return Result<TrainingCourseView>.Failure(NoCourse());
            }

            List<Guid> units = await db.CourseUnits.AsNoTracking().Where(u => u.CourseId == courseId).Select(u => u.Id).ToListAsync(ct).ConfigureAwait(false);
            if (!units.Contains(unitId))
            {
                return Result<TrainingCourseView>.Failure(Error.NotFound("training.no_unit", "That unit isn't part of this course."));
            }

            DateTimeOffset now = clock.StoredNow();
            DateOnly today = Today;

            // Upserts, so a double-click or a second device marking the same unit is no race.
            Guid newId = UuidV7.NewId();
            Guid tenantId = tenant.TenantId.Value;
            Guid person = user.Value;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"insert into workforce.course_enrollment (id, tenant_id, course_id, user_id, started_at) values ({newId}, {tenantId}, {courseId}, {person}, {now}) on conflict (course_id, user_id) do nothing",
                ct).ConfigureAwait(false);
            CourseEnrollmentRecord enrolment = await db.CourseEnrollments.FirstAsync(e => e.CourseId == courseId && e.UserId == person, ct).ConfigureAwait(false);

            if (enrolment.CompletedAt is not null && enrolment.ExpiresOn is { } ran && ran < today)
            {
                // Run out: it starts again, unit by unit.
                await db.UnitProgress.Where(p => p.CourseEnrollmentId == enrolment.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                enrolment.CompletedAt = null;
                enrolment.ExpiresOn = null;
                enrolment.StartedAt = now;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            if (enrolment.CompletedAt is not null)
            {
                return Result<TrainingCourseView>.Success(await TakingAsync(user, courseId, ct).ConfigureAwait(false));
            }

            Guid enrolmentId = enrolment.Id;
            int added = await db.Database.ExecuteSqlInterpolatedAsync(
                $"insert into workforce.course_unit_progress (course_enrollment_id, unit_id, completed_at) values ({enrolmentId}, {unitId}, {now}) on conflict do nothing",
                ct).ConfigureAwait(false);
            if (added > 0)
            {
                await AuditAsync(UnitDone, courseId, ct, ("unit", unitId.ToString())).ConfigureAwait(false);
            }

            int done = await db.UnitProgress.CountAsync(p => p.CourseEnrollmentId == enrolmentId && units.Contains(p.UnitId), ct).ConfigureAwait(false);
            if (done == units.Count)
            {
                enrolment.CompletedAt = now;
                enrolment.ExpiresOn = course.ValidityMonths is { } months ? today.AddMonths(months) : null;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await AuditAsync(CourseCompleted, courseId, ct,
                    ("expires_on", enrolment.ExpiresOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "never")).ConfigureAwait(false);
            }

            return Result<TrainingCourseView>.Success(await TakingAsync(user, courseId, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// Someone's training against what the county requires of the roles they hold now: as a mentor,
    /// as staff, and as an organisation's owner or admin (who count as staff too).
    /// </summary>
    public Task<TrainingStatus> StatusAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            IReadOnlyList<Requirement> requirements = await RequirementsForAsync(user, ct).ConfigureAwait(false);
            List<Guid> ids = [.. requirements.Select(r => r.CourseId)];
            Dictionary<Guid, CourseEnrollmentRecord> mine = await db.CourseEnrollments.AsNoTracking()
                .Where(e => e.UserId == user.Value && ids.Contains(e.CourseId)).ToDictionaryAsync(e => e.CourseId, ct).ConfigureAwait(false);
            DateOnly today = Today;
            List<RequiredCourse> required = [.. requirements.Select(r =>
            {
                CourseEnrollmentRecord? e = mine.GetValueOrDefault(r.CourseId);
                (string state, DateOnly? dueBy, bool current) = Standing(e, r.GraceDays, r.Since, today);
                return new RequiredCourse(r.CourseId, r.Slug, r.Title, r.AppliesTo, r.IsBlocking, state, e?.ExpiresOn, dueBy, current);
            })];
            return new TrainingStatus(required, required.Where(r => r.IsBlocking).All(r => r.Current));
        }, cancellationToken);

    /// <summary>
    /// Of these mentors' memberships, the people whose training counts for every blocking course
    /// the county requires of mentors: for search, which shows it (D130). Not audited: it's the
    /// caller's read.
    /// </summary>
    public Task<IReadOnlySet<Guid>> TrainedMentorsAsync(IReadOnlyList<OrganisationMembership> mentoring, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mentoring);
        return db.InTransactionAsync(ct => TrainedAsync(mentoring, ct), cancellationToken);
    }

    private async Task<IReadOnlySet<Guid>> TrainedAsync(IReadOnlyList<OrganisationMembership> mentoring, CancellationToken cancellationToken)
    {
        Dictionary<Guid, DateOnly> since = [];
        foreach (OrganisationMembership m in mentoring.Where(m => m.OrgRole == TrainingRoles.Mentor))
        {
            DateOnly joined = LocalDay(m.JoinedAt);
            since[m.UserId.Value] = since.TryGetValue(m.UserId.Value, out DateOnly earlier) && earlier < joined ? earlier : joined;
        }

        if (since.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var blocking = await (from r in db.TrainingRequirements.AsNoTracking()
                              join c in db.Courses.AsNoTracking() on r.CourseId equals c.Id
                              where r.AppliesTo == TrainingRoles.Mentor && r.IsBlocking && c.Status == CourseStatuses.Published
                              select new { r.CourseId, r.GraceDays })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (blocking.Count == 0)
        {
            return new HashSet<Guid>(since.Keys);
        }

        List<Guid> people = [.. since.Keys];
        List<Guid> courses = [.. blocking.Select(b => b.CourseId)];
        Dictionary<(Guid, Guid), CourseEnrollmentRecord> taken = (await db.CourseEnrollments.AsNoTracking()
                .Where(e => people.Contains(e.UserId) && courses.Contains(e.CourseId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => (e.UserId, e.CourseId));
        DateOnly today = Today;
        return new HashSet<Guid>(people.Where(p => blocking.All(b =>
            Standing(taken.GetValueOrDefault((p, b.CourseId)), b.GraceDays, since[p], today).Current)));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private DateOnly Today => clock.TodayIn(tenant.TimeZone);

    private DateOnly LocalDay(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tenant.TimeZone).DateTime);

    /// <summary>
    /// Where someone stands with a required course: held while completed and not past its date;
    /// otherwise current until the grace's last day, which runs from when their completion ran
    /// out, or else from when they took the role. No grace, no slack.
    /// </summary>
    private static (string State, DateOnly? DueBy, bool Current) Standing(CourseEnrollmentRecord? e, int graceDays, DateOnly since, DateOnly today)
    {
        string state = StateOf(e, today);
        if (state == TrainingStates.Completed)
        {
            return (state, null, true);
        }

        if (graceDays <= 0)
        {
            return (state, null, false);
        }

        DateOnly dueBy = (state == TrainingStates.Expired ? e!.ExpiresOn!.Value : since).AddDays(graceDays);
        return (state, dueBy, today <= dueBy);
    }

    /// <summary>A course the county requires of one of the person's roles, merged across roles: blocking if any is, with the least grace.</summary>
    private sealed record Requirement(Guid CourseId, string Slug, string Title, IReadOnlyList<string> AppliesTo, bool IsBlocking, int GraceDays, DateOnly Since);

    private async Task<IReadOnlyList<Requirement>> RequirementsForAsync(UserId user, CancellationToken ct)
    {
        IReadOnlyList<OrganisationMembership> memberships = await directory.GetMembershipsAsync(user, ct).ConfigureAwait(false);
        Dictionary<string, DateOnly> since = new(StringComparer.Ordinal);
        foreach (OrganisationMembership m in memberships)
        {
            foreach (string role in TrainingRolesOf(m.OrgRole))
            {
                DateOnly joined = LocalDay(m.JoinedAt);
                since[role] = since.TryGetValue(role, out DateOnly earlier) && earlier < joined ? earlier : joined;
            }
        }

        if (since.Count == 0)
        {
            return [];
        }

        List<string> roles = [.. since.Keys];
        var rows = await (from r in db.TrainingRequirements.AsNoTracking()
                          join c in db.Courses.AsNoTracking() on r.CourseId equals c.Id
                          where roles.Contains(r.AppliesTo) && c.Status == CourseStatuses.Published
                          select new { r.AppliesTo, r.CourseId, c.Slug, c.Title, r.IsBlocking, r.GraceDays })
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.GroupBy(r => r.CourseId).Select(g => new Requirement(
            g.Key,
            g.First().Slug,
            g.First().Title,
            [.. g.Select(r => r.AppliesTo).Order(StringComparer.Ordinal)],
            g.Any(r => r.IsBlocking),
            g.Min(r => (int)r.GraceDays),
            g.Max(r => since[r.AppliesTo])))
            .OrderBy(r => r.Title, StringComparer.CurrentCulture)];
    }

    private static string[] TrainingRolesOf(string orgRole) => orgRole switch
    {
        "owner" or "admin" => [TrainingRoles.OrgAdmin, TrainingRoles.Staff],
        "staff" => [TrainingRoles.Staff],
        "mentor" => [TrainingRoles.Mentor],
        _ => [],
    };

    private async Task<Error?> RefusedAsync(UserId user, CancellationToken ct)
    {
        if (await identity.IsMinorAsync(user, ct).ConfigureAwait(false))
        {
            return Error.Forbidden("training.adults_only", "Training is for the adults who work with organisations.");
        }

        return (await directory.GetMembershipsAsync(user, ct).ConfigureAwait(false)).Count == 0
            ? Error.Forbidden("training.no_role", "Training is for people who hold a role at an organisation.")
            : null;
    }

    private async Task<TrainingCourseView> TakingAsync(UserId user, Guid courseId, CancellationToken ct)
    {
        CourseView course = (await ViewAsync(courseId, ct).ConfigureAwait(false))!;
        CourseEnrollmentRecord? e = await db.CourseEnrollments.AsNoTracking().FirstOrDefaultAsync(x => x.CourseId == courseId && x.UserId == user.Value, ct).ConfigureAwait(false);
        List<Guid> done = e is null ? [] : await db.UnitProgress.AsNoTracking().Where(p => p.CourseEnrollmentId == e.Id).Select(p => p.UnitId).ToListAsync(ct).ConfigureAwait(false);
        bool required = (await RequirementsForAsync(user, ct).ConfigureAwait(false)).Any(r => r.CourseId == courseId);
        return new TrainingCourseView(course, StateOf(e, Today), done, e?.CompletedAt, e?.ExpiresOn, required);
    }

    private async Task<CourseView?> ViewAsync(Guid courseId, CancellationToken ct)
    {
        CourseRecord? c = await db.Courses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == courseId, ct).ConfigureAwait(false);
        if (c is null)
        {
            return null;
        }

        List<CourseUnitView> units = await db.CourseUnits.AsNoTracking().Where(u => u.CourseId == courseId).OrderBy(u => u.SortOrder)
            .Select(u => new CourseUnitView(u.Id, u.SortOrder, u.Title, u.BodyMd, u.ExternalUrl, u.Minutes)).ToListAsync(ct).ConfigureAwait(false);
        return new CourseView(c.Id, c.Slug, c.Title, c.Summary, c.ProviderName, c.Delivery, c.Hours, c.ValidityMonths, c.Status, c.PublishedAt, units);
    }

    private static string StateOf(CourseEnrollmentRecord? e, DateOnly today) => e switch
    {
        null => TrainingStates.NotStarted,
        { CompletedAt: null } => TrainingStates.InProgress,
        { ExpiresOn: { } until } when until < today => TrainingStates.Expired,
        _ => TrainingStates.Completed,
    };

    private static Error? CheckUnits(List<CourseUnitInput> units)
    {
        if (units.Count > MaxUnits)
        {
            return Error.Validation("training.too_many_units", $"Up to {MaxUnits} units.");
        }

        foreach (CourseUnitInput u in units)
        {
            string? title = Optional(u.Title);
            string? body = Optional(u.Body);
            string? link = Optional(u.Link);
            if (title is null or { Length: > MaxTitle } || body is { Length: > MaxBody })
            {
                return Error.Validation("training.unit_text", $"Each unit needs a title of up to {MaxTitle} characters, and text of up to {MaxBody}.");
            }

            if (body is null && link is null)
            {
                return Error.Validation("training.unit_empty", "Each unit needs text to read, a link to follow, or both.");
            }

            if (link is not null && (!Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps))
            {
                return Error.Validation("training.unit_link", "A unit's link starts with https://.");
            }

            if (u.Minutes is < 1 or > 600)
            {
                return Error.Validation("training.unit_minutes", "A unit takes 1 to 600 minutes, or say nothing.");
            }
        }

        return null;
    }

    private static bool IsBlank(CourseUnitInput u) =>
        Optional(u.Title) is null && Optional(u.Body) is null && Optional(u.Link) is null && u.Minutes is null;

    private Task AuditAsync(string action, Guid courseId, CancellationToken ct, params (string Key, string Value)[] detail) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "course",
            SubjectIds = [courseId],
            Detail = detail.Length == 0 ? null : detail.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
        }, ct);

    private static string? Optional(string? text) => text?.Trim() is { Length: > 0 } t ? t : null;

    private static Error NoCourse() => Error.NotFound("training.no_course", "There's no such course.");

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugShape();

    private static bool SlugOk(string slug) => slug.Length is >= 3 and <= 60 && SlugShape().IsMatch(slug);
}
