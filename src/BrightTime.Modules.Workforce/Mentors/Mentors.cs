using System.Globalization;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Platform.Contracts;
using BrightTime.Modules.Workforce.Contracts;
using BrightTime.Modules.Workforce.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Workforce.Mentors;

/// <summary>A time a mentor is usually free, in the county's time: 0 for Sunday to 6 for Saturday.</summary>
public sealed record AvailabilitySlot(int DayOfWeek, TimeOnly From, TimeOnly To);

/// <summary>A term of the county's taxonomy, by id, code and name.</summary>
public sealed record MentorTerm(Guid Id, string Code, string Name);

/// <summary>
/// A mentor's profile as its owner edits it (D129). <see cref="Listed"/> says whether they've asked
/// to be found; <see cref="Findable"/> whether staff can find them now: listed, with a mentor's
/// role at an organisation, and every clearance the county requires current.
/// </summary>
public sealed record MentorProfileView(
    string? DisplayName,
    string? Headline,
    string? Bio,
    IReadOnlyList<string> Languages,
    IReadOnlyList<Guid> Skills,
    Guid? HomeNeighbourhoodId,
    bool WillingVirtual,
    int? WeeklyHoursSought,
    int? MaxTravelM,
    IReadOnlyList<AvailabilitySlot> Availability,
    string Status,
    bool Listed,
    bool IsMentor,
    bool Findable,
    IReadOnlyList<string> Organisations);

/// <summary>What a mentor writes about themselves (D129). Null fields are left empty.</summary>
public sealed record MentorProfileInput(
    string? DisplayName,
    string? Headline,
    string? Bio,
    IReadOnlyList<string>? Languages,
    IReadOnlyList<Guid>? Skills,
    Guid? HomeNeighbourhoodId,
    bool WillingVirtual,
    int? WeeklyHoursSought,
    int? MaxTravelM,
    bool Listed);

/// <summary>What staff search mentors by (D129); none of it needed.</summary>
public sealed record MentorQuery(Guid? Skill, int? DayOfWeek, Guid? Neighbourhood, bool VirtualOnly);

/// <summary>
/// A mentor as an organisation's staff find them (D129): the name and words they wrote, what they
/// offer, when they're free, which organisations they mentor with, and their address to get in
/// touch. Adults' details, not a young person's. <see cref="TrainingCurrent"/> when their training
/// counts for every blocking course the county requires of mentors (D130).
/// </summary>
public sealed record MentorCard(
    Guid UserId,
    string DisplayName,
    string? Headline,
    string? Bio,
    IReadOnlyList<MentorTerm> Languages,
    IReadOnlyList<MentorTerm> Skills,
    string? Neighbourhood,
    bool WillingVirtual,
    int? WeeklyHoursSought,
    IReadOnlyList<AvailabilitySlot> Availability,
    IReadOnlyList<string> Organisations,
    string? Email,
    bool TrainingCurrent);

/// <summary>
/// Mentors (M6, D129). An organisation's admins make someone a mentor, as they make someone staff;
/// the mentor writes a profile, once, county-wide (what they offer, the languages they speak, where
/// they live and whether they'll meet online, the hours a week they want, and when they're usually
/// free) and lists it when they're ready. Organisations' staff search the listed profiles.
/// </summary>
/// <remarks>
/// <para>
/// A mentor is found only while they hold a mentor's role at an approved organisation and a current
/// clearance of every kind the county requires, recorded by that organisation or county-wide,
/// whether or not the county enforces the rules for staff (D99): a clearance that lapses overnight
/// takes them out of search by morning, with nothing for anyone to do.
/// </para>
/// <para>
/// Writing a profile needs <c>mentor_profile.manage_own</c> and a mentor's role somewhere; the
/// caller checks the permission. Searching needs <c>mentors.search</c> for the organisation, which
/// the caller checks too.
/// </para>
/// </remarks>
public sealed class Mentors(
    WorkforceDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IDirectoryQueries directory,
    IIdentityQueries identity,
    ITaxonomyQueries taxonomy,
    IGeographyQueries geography,
    IClearanceGate clearances,
    Training.Training training)
{
    public const string Read = "workforce.mentor_profile.read";
    public const string Saved = "workforce.mentor_profile.saved";
    public const string AvailabilitySet = "workforce.mentor_profile.availability_set";
    public const string Searched = "workforce.mentors.searched";

    /// <summary>The organisation role that makes someone a mentor (<c>ck_org_membership_role</c>).</summary>
    public const string MentorRole = "mentor";

    public const int MaxName = 80;
    public const int MaxHeadline = 120;
    public const int MaxBio = 2000;
    public const int MaxSkills = 20;
    public const int MaxLanguages = 10;
    public const int MaxSlots = 14;

    /// <summary>The person's own profile, or an empty one; with whether they're a mentor and can be found.</summary>
    public Task<MentorProfileView> MineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            MentorProfileView view = await ViewAsync(user, ct).ConfigureAwait(false);
            await AuditAsync(Read, user, ct).ConfigureAwait(false);
            return view;
        }, cancellationToken);

    /// <summary>Saves the person's profile, and lists it or not. Only someone with a mentor's role writes one.</summary>
    public Task<Result<MentorProfileView>> SaveAsync(UserId user, MentorProfileInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            if ((await MentorOrganisationsAsync(user, ct).ConfigureAwait(false)).Count == 0)
            {
                return Result<MentorProfileView>.Failure(NotMentor());
            }

            string? name = Optional(input.DisplayName);
            string? headline = Optional(input.Headline);
            string? bio = Optional(input.Bio);
            if (name is { Length: > MaxName } || headline is { Length: > MaxHeadline } || bio is { Length: > MaxBio })
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.text",
                    $"A name of up to {MaxName} characters, a headline of up to {MaxHeadline} and a bio of up to {MaxBio}."));
            }

            if (input.Listed && name is null)
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.name", "Give the name organisations will find you by before you list your profile."));
            }

            List<Guid> skills = [.. (input.Skills ?? []).Distinct()];
            List<string> languages = [.. (input.Languages ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.Ordinal)];
            if (skills.Count > MaxSkills || languages.Count > MaxLanguages)
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.too_many", $"Up to {MaxSkills} skills and {MaxLanguages} languages."));
            }

            if (skills.Count > 0)
            {
                IReadOnlyList<TermSummary> known = await taxonomy.GetTermsAsync(skills, tenant.Locale.Code, ct).ConfigureAwait(false);
                if (skills.Any(s => !known.Any(k => k.Id == s && k.Vocabulary == MentorVocabularies.Skill)))
                {
                    return Result<MentorProfileView>.Failure(Error.Validation("mentors.skills", "Choose skills from the county's list."));
                }
            }

            if (languages.Count > 0)
            {
                IReadOnlyList<VocabularyTerm> offered = await taxonomy.GetVocabularyAsync(MentorVocabularies.Language, tenant.Locale.Code, ct).ConfigureAwait(false);
                if (languages.Any(l => !offered.Any(o => o.Code == l)))
                {
                    return Result<MentorProfileView>.Failure(Error.Validation("mentors.languages", "Choose languages from the county's list."));
                }
            }

            if (input.HomeNeighbourhoodId is { } hood
                && !(await geography.GetNeighbourhoodNamesAsync([hood], tenant.Locale.Code, ct).ConfigureAwait(false)).ContainsKey(hood))
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.neighbourhood", "Choose one of the county's neighbourhoods, or none."));
            }

            if (input.WeeklyHoursSought is < 1 or > 60 || input.MaxTravelM is < 500 or > 160000)
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.numbers", "From 1 to 60 hours a week, and travel of 500 m to 160 km, or none."));
            }

            DateTimeOffset now = clock.StoredNow();
            MentorProfileRecord? profile = await db.MentorProfiles.FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
            if (profile is null)
            {
                profile = new MentorProfileRecord { UserId = user.Value, TenantId = tenant.TenantId.Value, Status = MentorStatuses.Draft, CreatedAt = now };
                db.MentorProfiles.Add(profile);
            }

            profile.DisplayName = name;
            profile.Headline = headline;
            profile.Bio = bio;
            profile.Languages = [.. languages];
            profile.SkillTermIds = [.. skills];
            profile.HomeNeighbourhoodId = input.HomeNeighbourhoodId;
            profile.WillingVirtual = input.WillingVirtual;
            profile.WeeklyHoursSought = (short?)input.WeeklyHoursSought;
            profile.MaxTravelM = input.MaxTravelM;
            profile.Status = input.Listed ? MentorStatuses.Active : profile.Status == MentorStatuses.Draft ? MentorStatuses.Draft : MentorStatuses.Paused;
            profile.IsSearchable = input.Listed;
            profile.UpdatedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(Saved, user, ct, ("listed", input.Listed ? "true" : "false")).ConfigureAwait(false);
            return Result<MentorProfileView>.Success(await ViewAsync(user, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Replaces the times the person is usually free. Saving a profile first isn't needed.</summary>
    public Task<Result<MentorProfileView>> SetAvailabilityAsync(UserId user, IReadOnlyList<AvailabilitySlot> slots, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(slots);
            if ((await MentorOrganisationsAsync(user, ct).ConfigureAwait(false)).Count == 0)
            {
                return Result<MentorProfileView>.Failure(NotMentor());
            }

            if (slots.Count > MaxSlots || slots.Any(s => s.DayOfWeek is < 0 or > 6 || s.To <= s.From))
            {
                return Result<MentorProfileView>.Failure(Error.Validation("mentors.availability", $"Up to {MaxSlots} times, each on a day of the week and ending after it starts."));
            }

            DateTimeOffset now = clock.StoredNow();
            if (!await db.MentorProfiles.AnyAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false))
            {
                db.MentorProfiles.Add(new MentorProfileRecord { UserId = user.Value, TenantId = tenant.TenantId.Value, Status = MentorStatuses.Draft, CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await db.MentorAvailability.Where(a => a.MentorUserId == user.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.MentorAvailability.AddRange(slots.OrderBy(s => s.DayOfWeek).ThenBy(s => s.From).Select(s => new MentorAvailabilityRecord
            {
                Id = UuidV7.NewId(),
                MentorUserId = user.Value,
                DayOfWeek = (short)s.DayOfWeek,
                StartsAtLocal = s.From,
                EndsAtLocal = s.To,
            }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await AuditAsync(AvailabilitySet, user, ct, ("slots", slots.Count.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
            return Result<MentorProfileView>.Success(await ViewAsync(user, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>
    /// The mentors an organisation's staff can find, by name: listed, with a mentor's role at an
    /// approved organisation, cleared, and matching what's asked. The search is audited.
    /// </summary>
    public Task<IReadOnlyList<MentorCard>> SearchAsync(OrganisationId org, MentorQuery query, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<IReadOnlyList<MentorCard>>(async ct =>
        {
            ArgumentNullException.ThrowIfNull(query);
            IQueryable<MentorProfileRecord> listed = db.MentorProfiles.AsNoTracking().Where(p => p.IsSearchable && p.Status == MentorStatuses.Active);
            if (query.Skill is { } skill)
            {
                listed = listed.Where(p => p.SkillTermIds.Contains(skill));
            }

            if (query.VirtualOnly)
            {
                listed = listed.Where(p => p.WillingVirtual);
            }
            else if (query.Neighbourhood is { } hood)
            {
                listed = listed.Where(p => p.HomeNeighbourhoodId == hood || p.WillingVirtual);
            }

            if (query.DayOfWeek is { } day)
            {
                short d = (short)day;
                listed = listed.Where(p => db.MentorAvailability.Any(a => a.MentorUserId == p.UserId && a.DayOfWeek == d));
            }

            List<MentorProfileRecord> profiles = await listed.OrderBy(p => p.DisplayName).ThenBy(p => p.UserId).Take(200).ToListAsync(ct).ConfigureAwait(false);
            IReadOnlyList<OrganisationMembership> mentoring = await directory.GetMembersWithRoleAsync(MentorRole, ct).ConfigureAwait(false);
            List<(Guid Person, Guid Organisation)> pairs = [.. mentoring.Where(m => profiles.Any(p => p.UserId == m.UserId.Value))
                .Select(m => (m.UserId.Value, m.OrganisationId.Value))];
            IReadOnlySet<Guid> cleared = await clearances.ClearedAsync(pairs, ct).ConfigureAwait(false);
            List<MentorProfileRecord> found = [.. profiles.Where(p => cleared.Contains(p.UserId))];

            List<MentorCard> cards = await CardsAsync(found, mentoring, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Searched,
                SubjectKind = "mentor_profile",
                SubjectIds = [.. found.Select(p => p.UserId)],
                ActorOrgId = org,
            }, ct).ConfigureAwait(false);
            return cards;
        }, cancellationToken);

    // ---- helpers -----------------------------------------------------------------------------

    private async Task<IReadOnlyList<OrganisationId>> MentorOrganisationsAsync(UserId user, CancellationToken ct)
    {
        IReadOnlyList<OrganisationMembership> mentoring = await directory.GetMembersWithRoleAsync(MentorRole, ct).ConfigureAwait(false);
        return [.. mentoring.Where(m => m.UserId == user).Select(m => m.OrganisationId).Distinct()];
    }

    private async Task<MentorProfileView> ViewAsync(UserId user, CancellationToken ct)
    {
        IReadOnlyList<OrganisationId> orgs = await MentorOrganisationsAsync(user, ct).ConfigureAwait(false);
        List<string> names = [];
        foreach (OrganisationId org in orgs)
        {
            names.Add(await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "");
        }

        MentorProfileRecord? p = await db.MentorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == user.Value, ct).ConfigureAwait(false);
        List<AvailabilitySlot> slots = (await SlotsAsync([user.Value], ct).ConfigureAwait(false)).GetValueOrDefault(user.Value, []);
        bool findable = p is { IsSearchable: true }
            && (await clearances.ClearedAsync([.. orgs.Select(o => (user.Value, o.Value))], ct).ConfigureAwait(false)).Contains(user.Value);
        return new MentorProfileView(
            p?.DisplayName, p?.Headline, p?.Bio, p?.Languages ?? [], p?.SkillTermIds ?? [], p?.HomeNeighbourhoodId, p?.WillingVirtual ?? true,
            p?.WeeklyHoursSought, p?.MaxTravelM, slots, p?.Status ?? MentorStatuses.Draft, p?.IsSearchable ?? false, orgs.Count > 0, findable, names);
    }

    private async Task<Dictionary<Guid, List<AvailabilitySlot>>> SlotsAsync(IReadOnlyCollection<Guid> users, CancellationToken ct) =>
        (await db.MentorAvailability.AsNoTracking().Where(a => users.Contains(a.MentorUserId))
            .OrderBy(a => a.DayOfWeek).ThenBy(a => a.StartsAtLocal).ToListAsync(ct).ConfigureAwait(false))
        .GroupBy(a => a.MentorUserId)
        .ToDictionary(g => g.Key, g => g.Select(a => new AvailabilitySlot(a.DayOfWeek, a.StartsAtLocal, a.EndsAtLocal)).ToList());

    private async Task<List<MentorCard>> CardsAsync(List<MentorProfileRecord> profiles, IReadOnlyList<OrganisationMembership> mentoring, CancellationToken ct)
    {
        if (profiles.Count == 0)
        {
            return [];
        }

        List<Guid> users = [.. profiles.Select(p => p.UserId)];
        Dictionary<Guid, List<AvailabilitySlot>> slots = await SlotsAsync(users, ct).ConfigureAwait(false);
        Dictionary<Guid, TermSummary> skills = (await taxonomy.GetTermsAsync([.. profiles.SelectMany(p => p.SkillTermIds).Distinct()], tenant.Locale.Code, ct).ConfigureAwait(false))
            .ToDictionary(t => t.Id);
        Dictionary<string, VocabularyTerm> languages = (await taxonomy.GetVocabularyAsync(MentorVocabularies.Language, tenant.Locale.Code, ct).ConfigureAwait(false))
            .ToDictionary(t => t.Code, StringComparer.Ordinal);
        IReadOnlyDictionary<Guid, string> hoods = await geography.GetNeighbourhoodNamesAsync(
            [.. profiles.Where(p => p.HomeNeighbourhoodId is not null).Select(p => p.HomeNeighbourhoodId!.Value).Distinct()], tenant.Locale.Code, ct).ConfigureAwait(false);
        IReadOnlyDictionary<Guid, string> emails = await identity.GetAdultEmailsAsync(users, ct).ConfigureAwait(false);
        IReadOnlySet<Guid> trained = await training.TrainedMentorsAsync([.. mentoring.Where(m => users.Contains(m.UserId.Value))], ct).ConfigureAwait(false);
        Dictionary<Guid, string> orgNames = [];
        foreach (OrganisationId org in mentoring.Where(m => users.Contains(m.UserId.Value)).Select(m => m.OrganisationId).Distinct())
        {
            orgNames[org.Value] = await directory.GetOrganisationNameAsync(org, ct).ConfigureAwait(false) ?? "";
        }

        return [.. profiles.Select(p => new MentorCard(
            p.UserId,
            p.DisplayName ?? "",
            p.Headline,
            p.Bio,
            [.. p.Languages.Where(languages.ContainsKey).Select(l => new MentorTerm(languages[l].Id, l, languages[l].Name))],
            [.. p.SkillTermIds.Where(skills.ContainsKey).Select(s => new MentorTerm(s, skills[s].Code, skills[s].Name ?? skills[s].Code))],
            p.HomeNeighbourhoodId is { } h ? hoods.GetValueOrDefault(h) : null,
            p.WillingVirtual,
            p.WeeklyHoursSought,
            slots.GetValueOrDefault(p.UserId, []),
            [.. mentoring.Where(m => m.UserId.Value == p.UserId).Select(m => orgNames.GetValueOrDefault(m.OrganisationId.Value, "")).Distinct()],
            emails.GetValueOrDefault(p.UserId),
            trained.Contains(p.UserId)))];
    }

    private Task AuditAsync(string action, UserId user, CancellationToken ct, params (string Key, string Value)[] detail) =>
        audit.RecordInTransactionAsync(db, new AuditAccess
        {
            Action = action,
            SubjectKind = "mentor_profile",
            SubjectIds = [user.Value],
            Detail = detail.Length == 0 ? null : detail.ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
        }, ct);

    private static string? Optional(string? text) => text?.Trim() is { Length: > 0 } t ? t : null;

    private static Error NotMentor() => Error.Forbidden("mentors.not_mentor", "Only someone an organisation has made a mentor writes a mentor's profile.");
}

public static class WorkforceServices
{
    /// <summary>The Workforce module's services (M6): mentors (D129) and training (D130). Needs the Workforce context, Directory's queries and its clearance gate.</summary>
    public static IServiceCollection AddWorkforce(this IServiceCollection services)
    {
        services.AddScoped<Mentors>();
        services.AddScoped<Training.Training>();
        return services;
    }
}
