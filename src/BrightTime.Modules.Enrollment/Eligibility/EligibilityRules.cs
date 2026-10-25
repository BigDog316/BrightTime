using System.Text.Json;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Enrollment.Persistence;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Enrollment.Eligibility;

/// <summary>
/// A rule's settings, one shape for every kind; only the fields its kind uses may be set.
/// </summary>
/// <param name="Min">Age or grade, inclusive.</param>
/// <param name="Max">Age or grade, inclusive.</param>
/// <param name="On">For an age rule, the date the age is taken on (a program's start); today when absent.</param>
/// <param name="NeighbourhoodIds">For a neighbourhood rule, where the young person must live.</param>
/// <param name="BadgeClassId">For a badge rule, the badge they must hold (D67).</param>
/// <param name="PlaylistId">For a playlist rule, the playlist they must have completed (D67).</param>
public sealed record RuleConfig(
    int? Min = null, int? Max = null, DateOnly? On = null, IReadOnlyList<Guid>? NeighbourhoodIds = null, Guid? BadgeClassId = null, Guid? PlaylistId = null);

public sealed record RuleInput(string Kind, RuleConfig Config, bool IsHard = true);

public sealed record RuleView(Guid Id, string Kind, RuleConfig Config, bool IsHard);

/// <summary>
/// What eligibility needs to know about a young person. Nothing else is read. What they have
/// achieved is their own account's (D67); without one, nothing.
/// </summary>
public sealed record Applicant(DateOnly? DateOfBirth, short? Grade, Guid? NeighbourhoodId, Achievements? Achieved = null);

/// <summary>One rule's outcome: met, not met, or not known (the family hasn't said).</summary>
public sealed record RuleOutcome(Guid? RuleId, string Kind, bool IsHard, bool? Met);

/// <summary>
/// The verdict: false when a hard rule is not met; null when none is failed but one can't be
/// decided yet; true otherwise. Advisory rules never change it.
/// </summary>
public sealed record EligibilityResult(bool? IsEligible, IReadOnlyList<RuleOutcome> Rules);

/// <summary>
/// A listing's eligibility rules (M3a, D46): declarative, shown to a family before they apply,
/// and evaluated here, never by code a provider writes.
/// </summary>
public sealed class EligibilityRules(
    EnrollmentDbContext db, ITenantContext tenant, IClock clock, IListingDetails listings, IGeographyQueries geography, IAchievementQueries achievements)
{
    public const int MaxRules = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<Result<IReadOnlyList<RuleView>>> GetAsync(OrganisationId org, Guid listingId, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
            await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false)
                ? Result<IReadOnlyList<RuleView>>.Success(await RulesAsync(listingId, ct).ConfigureAwait(false))
                : Result<IReadOnlyList<RuleView>>.Failure(NoListing()), cancellationToken);

    /// <summary>Replaces a listing's rules. Applications already submitted keep the verdict they were given.</summary>
    public Task<Result<IReadOnlyList<RuleView>>> SetAsync(OrganisationId org, Guid listingId, IReadOnlyList<RuleInput> rules, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(rules);
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<IReadOnlyList<RuleView>>.Failure(NoListing());
            }

            if (await CheckAsync(rules, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<IReadOnlyList<RuleView>>.Failure(invalid);
            }

            await db.EligibilityRules.Where(r => r.ListingId == listingId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < rules.Count; i++)
            {
                db.EligibilityRules.Add(new EligibilityRuleRecord
                {
                    Id = UuidV7.NewId(),
                    TenantId = tenant.TenantId.Value,
                    ListingId = listingId,
                    RuleKind = rules[i].Kind,
                    Config = JsonSerializer.Serialize(rules[i].Config, Json),
                    IsHard = rules[i].IsHard,
                    SortOrder = (short)i,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<IReadOnlyList<RuleView>>.Success(await RulesAsync(listingId, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// A dry run: how rules would judge a young person who doesn't exist. With no rules given, the
    /// listing's saved ones; with rules, those, so a provider tries a change before saving it.
    /// </summary>
    public Task<Result<EligibilityResult>> PreviewAsync(OrganisationId org, Guid listingId, IReadOnlyList<RuleInput>? rules, Applicant applicant, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(applicant);
            if (!await OwnsListingAsync(org, listingId, ct).ConfigureAwait(false))
            {
                return Result<EligibilityResult>.Failure(NoListing());
            }

            if (rules is not null && await CheckAsync(rules, ct).ConfigureAwait(false) is { } invalid)
            {
                return Result<EligibilityResult>.Failure(invalid);
            }

            IReadOnlyList<RuleView> judged = rules is null
                ? await RulesAsync(listingId, ct).ConfigureAwait(false)
                : [.. rules.Select(r => new RuleView(Guid.Empty, r.Kind, r.Config, r.IsHard))];
            return Result<EligibilityResult>.Success(Evaluate(judged, applicant));
        }, cancellationToken);

    /// <summary>A published listing's rules, for a family to read before applying; null when there is no such listing.</summary>
    public Task<IReadOnlyList<RuleView>?> ForPublishedListingAsync(string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<RuleView>?>(async ct =>
            await listings.GetPublishedAsync(tenant.TenantId, slug, Locale.Default.Code, ct).ConfigureAwait(false) is { } listing
                ? await RulesAsync(listing.Id.Value, ct).ConfigureAwait(false)
                : null, cancellationToken);

    /// <summary>A listing's saved rules, judged against a young person.</summary>
    public async Task<EligibilityResult> EvaluateAsync(Guid listingId, Applicant applicant, CancellationToken cancellationToken = default) =>
        Evaluate(await RulesAsync(listingId, cancellationToken).ConfigureAwait(false), applicant);

    public EligibilityResult Evaluate(IReadOnlyList<RuleView> rules, Applicant applicant)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(applicant);
        DateOnly today = clock.TodayIn(tenant.TimeZone);
        List<RuleOutcome> outcomes = [.. rules.Select(r => new RuleOutcome(r.Id == Guid.Empty ? null : r.Id, r.Kind, r.IsHard, Judge(r, applicant, today)))];
        bool? verdict = outcomes.Any(o => o.IsHard && o.Met == false) ? false
            : outcomes.Any(o => o.IsHard && o.Met is null) ? null
            : true;
        return new EligibilityResult(verdict, outcomes);
    }

    public static string ToJson(EligibilityResult result) => JsonSerializer.Serialize(result, Json);

    private static bool? Judge(RuleView rule, Applicant who, DateOnly today) => rule.Kind switch
    {
        EligibilityRuleKinds.AgeAtStart => who.DateOfBirth is { } dob ? Within(Age.YearsOn(dob, rule.Config.On ?? today), rule.Config) : null,
        EligibilityRuleKinds.GradeRange => who.Grade is { } grade ? Within(grade, rule.Config) : null,
        EligibilityRuleKinds.NeighbourhoodIn => who.NeighbourhoodId is { } hood ? rule.Config.NeighbourhoodIds?.Contains(hood) == true : null,
        EligibilityRuleKinds.BadgeRequired => (who.Achieved ?? Achievements.None).BadgeClasses.Contains(rule.Config.BadgeClassId ?? Guid.Empty),
        EligibilityRuleKinds.PlaylistCompleted => (who.Achieved ?? Achievements.None).PlaylistsCompleted.Contains(rule.Config.PlaylistId ?? Guid.Empty),
        _ => null,
    };

    private static bool Unranged(RuleConfig c) => c.Min is null && c.Max is null && c.On is null && c.NeighbourhoodIds is null;

    private static bool Within(int value, RuleConfig config) => (config.Min is null || value >= config.Min) && (config.Max is null || value <= config.Max);

    private async Task<Error?> CheckAsync(IReadOnlyList<RuleInput> rules, CancellationToken ct)
    {
        if (rules.Count > MaxRules)
        {
            return Error.Validation("eligibility.too_many", $"At most {MaxRules} rules.");
        }

        for (int i = 0; i < rules.Count; i++)
        {
            RuleInput rule = rules[i];
            Dictionary<string, object> at = new(StringComparer.Ordinal) { ["rule"] = i + 1 };
            if (rule?.Kind is null || !EligibilityRuleKinds.Supported.Contains(rule.Kind) || rule.Config is null)
            {
                return Error.Validation("eligibility.kind", "One of " + string.Join(", ", EligibilityRuleKinds.Supported.Order(StringComparer.Ordinal)) + ".", arguments: at);
            }

            RuleConfig c = rule.Config;
            bool achievement = rule.Kind is EligibilityRuleKinds.BadgeRequired or EligibilityRuleKinds.PlaylistCompleted;
            bool fits = (achievement || (c.BadgeClassId is null && c.PlaylistId is null)) && rule.Kind switch
            {
                EligibilityRuleKinds.AgeAtStart => (c.Min is not null || c.Max is not null) && c.Min is null or (>= 0 and <= 25) && c.Max is null or (>= 0 and <= 25)
                    && (c.Min is null || c.Max is null || c.Min <= c.Max) && c.NeighbourhoodIds is null,
                EligibilityRuleKinds.GradeRange => (c.Min is not null || c.Max is not null) && c.Min is null or (>= -1 and <= 12) && c.Max is null or (>= -1 and <= 12)
                    && (c.Min is null || c.Max is null || c.Min <= c.Max) && c.On is null && c.NeighbourhoodIds is null,
                EligibilityRuleKinds.NeighbourhoodIn => c.NeighbourhoodIds is { Count: > 0 and <= 200 } && c.Min is null && c.Max is null && c.On is null,
                EligibilityRuleKinds.BadgeRequired => c.BadgeClassId is not null && c.PlaylistId is null && Unranged(c),
                EligibilityRuleKinds.PlaylistCompleted => c.PlaylistId is not null && c.BadgeClassId is null && Unranged(c),
                _ => false,
            };
            if (!fits)
            {
                return Error.Validation("eligibility.config", "Settings that suit the rule: an age or grade range, neighbourhoods, a badge or a playlist.", arguments: at);
            }

            if (achievement && await achievements.NameAsync(rule.Kind, (c.BadgeClassId ?? c.PlaylistId)!.Value, ct).ConfigureAwait(false) is null)
            {
                return Error.Validation("eligibility.achievement", "A published badge or playlist of this county.", arguments: at);
            }

            if (c.NeighbourhoodIds is { } ids)
            {
                IReadOnlyDictionary<Guid, string> known = await geography.GetNeighbourhoodNamesAsync([.. ids], Locale.Default.Code, ct).ConfigureAwait(false);
                if (ids.Any(id => !known.ContainsKey(id)))
                {
                    return Error.Validation("eligibility.config", "Neighbourhoods of this county.", arguments: at);
                }
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<RuleView>> RulesAsync(Guid listingId, CancellationToken ct) =>
        [.. (await db.EligibilityRules.AsNoTracking().Where(r => r.ListingId == listingId).OrderBy(r => r.SortOrder).ToListAsync(ct).ConfigureAwait(false))
            .Select(r => new RuleView(r.Id, r.RuleKind, JsonSerializer.Deserialize<RuleConfig>(r.Config, Json) ?? new RuleConfig(), r.IsHard))];

    private async Task<bool> OwnsListingAsync(OrganisationId org, Guid listingId, CancellationToken ct) =>
        (await listings.GetForReviewAsync(tenant.TenantId, listingId, Locale.Default.Code, ct).ConfigureAwait(false))?.OrganisationId == org;

    private static Error NoListing() => Error.NotFound("form.listing_not_found", "Your organisation has no listing with that id.");
}
