using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Records;

/// <summary>One skill on a young person's portfolio: their own assessment, or an organisation's endorsement.</summary>
[YouthData]
public sealed record SkillView(Guid Id, Guid TermId, string Name, short? Level, bool Endorsed, string? EndorsedBy, DateTimeOffset AssertedAt);

/// <summary>
/// A young person's skills (M4, D69), from the county's <c>skill</c> vocabulary: those they list
/// themselves, at a level they choose, and those an organisation they're enrolled with endorses.
/// They can remove any of them; the portfolio is theirs.
/// </summary>
public sealed class Skills(
    LearningDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    ITaxonomyQueries taxonomy,
    IEnrolmentQueries enrolments,
    IDirectoryQueries directory)
{
    public const string Read = "learning.skill.read";
    public const string Changed = "learning.skill.changed";
    public const string Endorsed = "learning.skill.endorsed";

    private const string SelfAssessment = "self_assessment";
    private const string Participation = "participation";

    public Task<IReadOnlyList<SkillView>> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<SkillView>>(async ct =>
        {
            IReadOnlyList<SkillView> mine = await OfAsync(user.Value, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "learner_skill", SubjectIds = [user.Value] }, ct).ConfigureAwait(false);
            return mine;
        }, cancellationToken);

    /// <summary>Lists a skill as the young person's own, or changes the level they gave it.</summary>
    public Task<Result<SkillView>> SetOwnAsync(UserId user, Guid termId, short? level, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await CheckAsync(termId, level, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<SkillView>.Failure(invalid);
            }

            LearnerSkillRecord? own = await db.Skills.FirstOrDefaultAsync(s => s.UserId == user.Value && s.TermId == termId && s.EvidenceKind == SelfAssessment, ct).ConfigureAwait(false);
            if (own is null)
            {
                own = new LearnerSkillRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    UserId = user.Value,
                    TermId = termId,
                    EvidenceKind = SelfAssessment,
                    AssertedBy = user.Value,
                };
                db.Skills.Add(own);
            }

            own.Level = level;
            own.AssertedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Changed, own.Id, null, ct).ConfigureAwait(false);
            return Result<SkillView>.Success((await OfAsync(user.Value, ct).ConfigureAwait(false)).First(s => s.Id == own.Id));
        }, cancellationToken);

    /// <summary>Takes a skill off the portfolio, endorsed or not.</summary>
    public Task<Result> RemoveAsync(UserId user, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            int removed = await db.Skills.Where(s => s.Id == id && s.UserId == user.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            if (removed == 0)
            {
                return Result.Failure(Error.NotFound("skills.not_found", "Your portfolio has no skill like that."));
            }

            await AuditAsync(Changed, id, null, ct, "removed").ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>An organisation vouches for a skill of a young person enrolled with it. Endorsing again changes the level.</summary>
    public Task<Result> EndorseAsync(OrganisationId org, UserId staff, Guid enrollmentId, Guid termId, short? level, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await CheckAsync(termId, level, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result.Failure(invalid);
            }

            IReadOnlyList<EnrolmentFacts> found = await enrolments.FactsAsync([enrollmentId], DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct).ConfigureAwait(false);
            if (found.Count == 0 || found[0].OrgId != org.Value || found[0].YouthUserId is not { } youth)
            {
                return Result.Failure(Error.Validation("skills.recipient",
                    "Choose a young person enrolled with your organisation whose family has linked them to their own account."));
            }

            LearnerSkillRecord? endorsement = await db.Skills.FirstOrDefaultAsync(
                s => s.UserId == youth && s.TermId == termId && s.EvidenceKind == Participation && s.EvidenceRefId == enrollmentId, ct).ConfigureAwait(false);
            if (endorsement is null)
            {
                endorsement = new LearnerSkillRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    UserId = youth,
                    TermId = termId,
                    EvidenceKind = Participation,
                    EvidenceRefId = enrollmentId,
                    AssertedBy = staff.Value,
                };
                db.Skills.Add(endorsement);
            }

            endorsement.Level = level;
            endorsement.AssertedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Endorsed, endorsement.Id, org, ct, youth: youth).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>A young person's skills, named in the request's language, with the organisation behind each endorsement.</summary>
    internal async Task<IReadOnlyList<SkillView>> OfAsync(Guid userId, CancellationToken ct)
    {
        List<LearnerSkillRecord> skills = await db.Skills.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct).ConfigureAwait(false);
        if (skills.Count == 0)
        {
            return [];
        }

        Dictionary<Guid, string> names = (await taxonomy.GetTermsAsync([.. skills.Select(s => s.TermId).Distinct()], tenant.Locale.Code, ct).ConfigureAwait(false))
            .ToDictionary(t => t.Id, t => t.Name ?? t.Code);
        Guid[] enrollments = [.. skills.Where(s => s.EvidenceKind == Participation && s.EvidenceRefId is not null).Select(s => s.EvidenceRefId!.Value).Distinct()];
        Dictionary<Guid, string?> vouchers = [];
        if (enrollments.Length > 0)
        {
            foreach (EnrolmentSummary e in await enrolments.GetAsync(enrollments, ct).ConfigureAwait(false))
            {
                vouchers[e.Id] = await directory.GetOrganisationNameAsync(new OrganisationId(e.OrgId), ct).ConfigureAwait(false);
            }
        }

        return [.. skills
            .Select(s => new SkillView(s.Id, s.TermId, names.GetValueOrDefault(s.TermId) ?? string.Empty, s.Level, s.EvidenceKind == Participation,
                s.EvidenceKind == Participation && s.EvidenceRefId is { } e ? vouchers.GetValueOrDefault(e) : null, s.AssertedAt))
            .OrderBy(s => s.Name, StringComparer.CurrentCulture)];
    }

    private async Task<Error?> CheckAsync(Guid termId, short? level, CancellationToken ct)
    {
        if (level is < 1 or > 4)
        {
            return Error.Validation("skills.level", "A level from 1 (beginning) to 4 (advanced), or none.");
        }

        return await taxonomy.GetTermsAsync([termId], "en", ct).ConfigureAwait(false) is [{ Vocabulary: "skill" }, ..]
            ? null
            : Error.Validation("skills.term", "A skill from this county's list.");
    }

    private Task AuditAsync(string action, Guid skill, OrganisationId? org, CancellationToken ct, string? what = null, Guid? youth = null) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "learner_skill",
            SubjectIds = youth is { } y ? [skill, y] : [skill],
            ActorOrgId = org,
            Detail = what is null ? new Dictionary<string, string>() : new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = what },
        }, ct);
}
