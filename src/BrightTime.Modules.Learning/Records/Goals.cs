using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Records;

public sealed record GoalInput(string? Title, string? Detail, Guid? TargetTermId, DateOnly? TargetDate, string? Status);

[YouthData]
public sealed record GoalView(Guid Id, string Title, string? Detail, Guid? TargetTermId, string? Target, DateOnly? TargetDate, string Status, DateTimeOffset CreatedAt, DateTimeOffset? AchievedAt);

/// <summary>
/// A young person's goals (M4, D69): their own aims, about an interest or a skill if they like.
/// Private: never on a shown portfolio, never in an export.
/// </summary>
public sealed class Goals(LearningDbContext db, ITenantContext tenant, AuditTrail audit, IClock clock, ITaxonomyQueries taxonomy)
{
    public const int MaxActive = 50;

    public const string Read = "learning.goal.read";
    public const string Changed = "learning.goal.changed";

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "active", "achieved", "paused", "abandoned" };

    public Task<IReadOnlyList<GoalView>> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<GoalView>>(async ct =>
        {
            List<LearnerGoalRecord> goals = await db.Goals.AsNoTracking().Where(g => g.UserId == user.Value)
                .OrderBy(g => g.Status == "active" ? 0 : 1).ThenByDescending(g => g.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "learner_goal", SubjectIds = [user.Value] }, ct).ConfigureAwait(false);
            return await ViewsAsync(goals, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task<Result<GoalView>> CreateAsync(UserId user, GoalInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await CheckAsync(input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<GoalView>.Failure(invalid);
            }

            if (await db.Goals.CountAsync(g => g.UserId == user.Value && g.Status == "active", ct).ConfigureAwait(false) >= MaxActive)
            {
                return Result<GoalView>.Failure(Error.Conflict("goals.too_many", $"Up to {MaxActive} goals at once. Mark some achieved or set them aside first."));
            }

            DateTimeOffset now = clock.StoredNow();
            LearnerGoalRecord goal = new()
            {
                Id = UuidV7.NewId(),
                TenantId = tenant.TenantId.Value,
                UserId = user.Value,
                Title = input.Title!.Trim(),
                Status = "active",
                CreatedAt = now,
            };
            Apply(goal, input, now);
            db.Goals.Add(goal);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(goal, "created", ct).ConfigureAwait(false);
            return Result<GoalView>.Success((await ViewsAsync([goal], ct).ConfigureAwait(false))[0]);
        }, cancellationToken);

    /// <summary>Changes a goal: its words, its target, or where it stands.</summary>
    public Task<Result<GoalView>> UpdateAsync(UserId user, Guid id, GoalInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if (await db.Goals.FirstOrDefaultAsync(g => g.Id == id && g.UserId == user.Value, ct).ConfigureAwait(false) is not { } goal)
            {
                return Result<GoalView>.Failure(Error.NotFound("goals.not_found", "You have no goal like that."));
            }

            if (await CheckAsync(input, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<GoalView>.Failure(invalid);
            }

            goal.Title = input.Title!.Trim();
            Apply(goal, input, clock.StoredNow());
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(goal, "changed", ct).ConfigureAwait(false);
            return Result<GoalView>.Success((await ViewsAsync([goal], ct).ConfigureAwait(false))[0]);
        }, cancellationToken);

    private static void Apply(LearnerGoalRecord goal, GoalInput input, DateTimeOffset now)
    {
        goal.Detail = input.Detail?.Trim() is { Length: > 0 } d ? d : null;
        goal.TargetTermId = input.TargetTermId;
        goal.TargetDate = input.TargetDate;
        string status = input.Status ?? goal.Status;
        goal.AchievedAt = status == "achieved" ? goal.AchievedAt ?? now : null;
        goal.Status = status;
    }

    private async Task<Error?> CheckAsync(GoalInput input, CancellationToken ct)
    {
        if (input.Title?.Trim() is not { Length: > 0 and <= 200 } || input.Detail is { Length: > 2000 })
        {
            return Error.Validation("goals.text", "A title of up to 200 characters, and up to 2,000 more about it.");
        }

        if (input.Status is { } status && !Statuses.Contains(status))
        {
            return Error.Validation("goals.status", "Active, achieved, paused or abandoned.");
        }

        if (input.TargetTermId is { } term
            && await taxonomy.GetTermsAsync([term], "en", ct).ConfigureAwait(false) is not [{ Vocabulary: "interest" or "skill" }, ..])
        {
            return Error.Validation("goals.target", "An interest or a skill from this county's lists, or none.");
        }

        return null;
    }

    private async Task<IReadOnlyList<GoalView>> ViewsAsync(IReadOnlyList<LearnerGoalRecord> goals, CancellationToken ct)
    {
        Guid[] terms = [.. goals.Where(g => g.TargetTermId is not null).Select(g => g.TargetTermId!.Value).Distinct()];
        Dictionary<Guid, string?> names = terms.Length == 0 ? [] : (await taxonomy.GetTermsAsync(terms, tenant.Locale.Code, ct).ConfigureAwait(false)).ToDictionary(t => t.Id, t => t.Name);
        return [.. goals.Select(g => new GoalView(g.Id, g.Title, g.Detail, g.TargetTermId, g.TargetTermId is { } t ? names.GetValueOrDefault(t) : null,
            g.TargetDate, g.Status, g.CreatedAt, g.AchievedAt))];
    }

    private Task AuditAsync(LearnerGoalRecord goal, string what, CancellationToken ct) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = Changed,
            SubjectKind = "learner_goal",
            SubjectIds = [goal.Id],
            Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["what"] = what, ["status"] = goal.Status },
        }, ct);
}
