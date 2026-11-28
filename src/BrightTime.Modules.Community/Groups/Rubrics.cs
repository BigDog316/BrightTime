using System.Globalization;
using BrightTime.Abstractions.Screening;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Community.Messaging;
using BrightTime.Modules.Community.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Community.Groups;

/// <summary>One of a rubric's criteria: what's scored, what a good score looks like if it says, and how much it counts.</summary>
public sealed record RubricCriterionView(Guid Id, string Label, string? Descriptor, decimal Weight);

/// <summary>
/// A rubric (D125). <see cref="InUse"/> says whether feedback has scored with it, which fixes it;
/// <see cref="Archived"/> that it's no longer offered for new challenges.
/// </summary>
public sealed record RubricView(Guid Id, string Name, int ScaleMax, IReadOnlyList<RubricCriterionView> Criteria, bool InUse, bool Archived, DateTimeOffset UpdatedAt);

/// <summary>A criterion as a rubric is written: its label, a descriptor if any, and its weight (1 when none is given).</summary>
public sealed record RubricCriterionInput(string? Label, string? Descriptor, decimal? Weight);

/// <summary>
/// An organisation's rubrics (M5, D125): written once by its admins and chosen for any of its
/// challenges as it's set. Each criterion is scored from 0 to the rubric's scale, and counts by its
/// weight. A rubric can be changed until feedback scores with it; then it's fixed, so a score always
/// means what it meant. Archived, it's no longer offered, and challenges that have it keep it.
/// </summary>
/// <remarks>
/// Criteria are shown to young people with the challenge, so they're screened, as a brief is, for an
/// attempt to take them off the platform (D53), and refused with why. The caller has checked who may
/// write them: an admin of the organisation who gives feedback.
/// </remarks>
public sealed class Rubrics(CommunityDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, IScreeningService screener)
{
    public const string Saved = "rubrics.saved";
    public const string Archived = "rubrics.archived";

    public const int MaxName = 120;
    public const int MaxLabel = 120;
    public const int MaxDescriptor = 500;
    public const int MaxCriteria = 8;
    public const int MinScale = 2;
    public const int MaxScale = 10;
    public const int DefaultScale = 4;
    public const decimal MaxWeight = 10m;

    /// <summary>The organisation's rubrics, by name; archived ones only when asked.</summary>
    public Task<IReadOnlyList<RubricView>> ListAsync(OrganisationId org, bool includeArchived, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RubricView>>(async ct =>
        {
            List<RubricRecord> rubrics = await db.Rubrics.AsNoTracking()
                .Where(r => r.OrgId == org.Value && (includeArchived || r.ArchivedAt == null))
                .OrderBy(r => r.Name).ThenBy(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
            return await ViewsAsync(db, rubrics, ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>One of the organisation's rubrics, or null.</summary>
    public Task<RubricView?> GetAsync(OrganisationId org, Guid id, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await db.Rubrics.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OrgId == org.Value, ct).ConfigureAwait(false) is { } rubric
                ? (await ViewsAsync(db, [rubric], ct).ConfigureAwait(false))[0]
                : null, cancellationToken);

    /// <summary>Writes a new rubric (<paramref name="id"/> null), or changes one no feedback has scored with yet.</summary>
    public Task<Result<Guid>> SaveAsync(
        UserId admin, OrganisationId org, Guid? id, string? name, int scaleMax, IReadOnlyList<RubricCriterionInput> criteria, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(criteria);
            if (name?.Trim() is not { Length: > 0 and <= MaxName } named)
            {
                return Result<Guid>.Failure(Error.Validation("rubrics.name", $"A name of 1 to {MaxName} characters."));
            }

            if (scaleMax is < MinScale or > MaxScale)
            {
                return Result<Guid>.Failure(Error.Validation("rubrics.scale", $"Scores from 0 up to {MinScale} to {MaxScale}."));
            }

            List<(string Label, string? Descriptor, decimal Weight)> given = [];
            foreach (RubricCriterionInput c in criteria)
            {
                string? label = c.Label?.Trim() is { Length: > 0 } l ? l : null;
                string? descriptor = c.Descriptor?.Trim() is { Length: > 0 } d ? d : null;
                if (label is null && descriptor is null)
                {
                    continue;
                }

                decimal weight = c.Weight ?? 1m;
                if (label is not { Length: <= MaxLabel } || descriptor is { Length: > MaxDescriptor } || weight <= 0 || weight > MaxWeight
                    || decimal.Round(weight, 2) != weight)
                {
                    return Result<Guid>.Failure(Error.Validation("rubrics.criterion",
                        $"Each criterion needs a label of up to {MaxLabel} characters, a description of up to {MaxDescriptor} if you like, and a weight above 0 and up to {MaxWeight}."));
                }

                given.Add((label, descriptor, weight));
            }

            if (given.Count is 0 or > MaxCriteria)
            {
                return Result<Guid>.Failure(Error.Validation("rubrics.criteria", $"From 1 to {MaxCriteria} criteria."));
            }

            string words = string.Join('\n', given.Select(g => g.Label + (g.Descriptor is null ? "" : "\n" + g.Descriptor)).Prepend(named));
            ScreenVerdict? verdict = await ContentScreening.ScreenAsync(screener, words, MessageScreen.ContactExchange, audienceIncludesMinor: true, "rubric", ct).ConfigureAwait(false);
            if (verdict is { Passed: false })
            {
                return Result<Guid>.Failure(Error.Validation("rubrics.screened", $"It looks like a way to talk away from BrightTime ({string.Join(", ", verdict.Reasons)}). Take that out and try again."));
            }

            DateTimeOffset now = clock.StoredNow();
            RubricRecord? rubric;
            if (id is { } existing)
            {
                rubric = await db.Rubrics.FirstOrDefaultAsync(r => r.Id == existing && r.OrgId == org.Value, ct).ConfigureAwait(false);
                if (rubric is null)
                {
                    return Result<Guid>.Failure(NotFound());
                }

                if (await InUseAsync(db, rubric.Id, ct).ConfigureAwait(false))
                {
                    return Result<Guid>.Failure(Error.Conflict("rubrics.in_use", "Feedback has been scored with this rubric, so it can't change. Write a new one."));
                }

                rubric.Name = named;
                rubric.ScaleMax = (short)scaleMax;
                rubric.UpdatedAt = now;
                await db.RubricCriteria.Where(c => c.RubricId == rubric.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }
            else
            {
                rubric = new RubricRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    OrgId = org.Value,
                    Name = named,
                    ScaleMax = (short)scaleMax,
                    CreatedBy = admin.Value,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Rubrics.Add(rubric);
            }

            for (int i = 0; i < given.Count; i++)
            {
                db.RubricCriteria.Add(new RubricCriterionRecord
                {
                    Id = UuidV7.NewId(),
                    RubricId = rubric.Id,
                    Code = "c" + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Label = given[i].Label,
                    DescriptorMd = given[i].Descriptor,
                    Weight = given[i].Weight,
                    SortOrder = (short)i,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Saved, rubric.Id, org, ct).ConfigureAwait(false);
            return Result<Guid>.Success(rubric.Id);
        }, cancellationToken);

    /// <summary>Archives a rubric, so it's no longer offered for new challenges, or offers it again.</summary>
    public Task<Result> ArchiveAsync(OrganisationId org, Guid id, bool archived, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            if (await db.Rubrics.FirstOrDefaultAsync(r => r.Id == id && r.OrgId == org.Value, ct).ConfigureAwait(false) is not { } rubric)
            {
                return Result.Failure(NotFound());
            }

            rubric.ArchivedAt = archived ? clock.StoredNow() : null;
            rubric.UpdatedAt = clock.StoredNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Archived, rubric.Id, org, ct, ("archived", archived ? "true" : "false")).ConfigureAwait(false);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Whether feedback has scored with any of the rubric's criteria.</summary>
    internal static Task<bool> InUseAsync(CommunityDbContext db, Guid rubricId, CancellationToken ct) =>
        db.FeedbackScores.AnyAsync(s => db.RubricCriteria.Any(c => c.Id == s.CriterionId && c.RubricId == rubricId), ct);

    /// <summary>The rubrics as views, with their criteria in order and whether each is in use.</summary>
    internal static async Task<IReadOnlyList<RubricView>> ViewsAsync(CommunityDbContext db, IReadOnlyList<RubricRecord> rubrics, CancellationToken ct)
    {
        List<Guid> ids = [.. rubrics.Select(r => r.Id)];
        ILookup<Guid, RubricCriterionRecord> criteria = (await db.RubricCriteria.AsNoTracking().Where(c => ids.Contains(c.RubricId))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct).ConfigureAwait(false)).ToLookup(c => c.RubricId);
        HashSet<Guid> used = [.. await db.RubricCriteria.AsNoTracking()
            .Where(c => ids.Contains(c.RubricId) && db.FeedbackScores.Any(s => s.CriterionId == c.Id))
            .Select(c => c.RubricId).Distinct().ToListAsync(ct).ConfigureAwait(false)];
        return [.. rubrics.Select(r => new RubricView(
            r.Id, r.Name, r.ScaleMax,
            [.. criteria[r.Id].Select(c => new RubricCriterionView(c.Id, c.Label, c.DescriptorMd, c.Weight))],
            used.Contains(r.Id), r.ArchivedAt is not null, r.UpdatedAt))];
    }

    private Task AuditAsync(string action, Guid rubric, OrganisationId org, CancellationToken ct, params (string Key, string Value)[] detail) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "rubric",
            SubjectIds = [rubric],
            ActorOrgId = org,
            Detail = detail.Length == 0 ? null : detail.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
        }, ct);

    private static Error NotFound() => Error.NotFound("rubrics.not_found", "Your organisation has no rubric like that.");
}
