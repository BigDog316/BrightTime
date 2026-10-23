using System.Text.RegularExpressions;
using BrightTime.Infrastructure.Auditing;
using BrightTime.Infrastructure.Outbox;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Privacy;
using BrightTime.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BrightTime.Modules.Learning.Portfolio;

[YouthData]
public sealed record ParticipationView(Guid Id, string Title, string? Organisation, DateOnly StartedOn, DateOnly? EndedOn, int SessionsAttended, decimal Hours, string Source, bool IsHidden);

/// <summary>A young person's own portfolio, as they see it: everything, hidden records included.</summary>
[YouthData]
public sealed record PortfolioView(
    string? DisplayName,
    string? Headline,
    string? Bio,
    string Visibility,
    string? PublicSlug,
    decimal TotalHours,
    bool CanShow,
    IReadOnlyList<ParticipationView> Participation);

/// <summary>A portfolio as anyone with its link sees it: no account, no legal name, nothing hidden.</summary>
[YouthData]
public sealed record PublicParticipation(string Title, string? Organisation, DateOnly StartedOn, DateOnly? EndedOn, int SessionsAttended, decimal Hours);

[YouthData]
public sealed record PublicPortfolio(string DisplayName, string? Headline, string? Bio, bool Listed, decimal TotalHours, IReadOnlyList<PublicParticipation> Participation);

public sealed record PortfolioInput(string? DisplayName, string? Headline, string? Bio, string? Visibility, string? PublicSlug);

/// <summary>
/// A young person's portfolio (M4, D65): what they took part in, and who may see it.
/// </summary>
/// <remarks>
/// <para>
/// The portfolio belongs to the young person. It is private until they choose otherwise, and they
/// can hide any record from it. Showing it to anyone with the link, or to everyone, needs the
/// showcase capability (no one under 13), a link name, and a display name that doesn't carry their
/// family name.
/// </para>
/// <para>
/// A shown portfolio is read by people who aren't signed in, and participation sits behind the
/// actor guard. So it is read through <c>learning.public_participation()</c>, which returns only
/// the unhidden records of a portfolio its owner chose to show, and nothing else.
/// </para>
/// </remarks>
public sealed partial class Portfolios(
    LearningDbContext db,
    ITenantContext tenant,
    AuditTrail audit,
    IClock clock,
    IIdentityQueries identity,
    IEnrolmentQueries enrolments,
    ParticipationProjector projector)
{
    public const int MaxDisplayName = 60;
    public const int MaxHeadline = 120;
    public const int MaxBio = 2000;

    public const string Read = "learning.portfolio.read";
    public const string Changed = "learning.portfolio.changed";
    public const string PublicRead = "learning.portfolio.public_read";

    public Task<PortfolioView> GetMineAsync(UserId user, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            PortfolioView view = await MineCoreAsync(user, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = Read, SubjectKind = "learner_profile", SubjectIds = [user.Value] }, ct).ConfigureAwait(false);
            return view;
        }, cancellationToken);

    /// <summary>Sets how the portfolio appears and who may see it.</summary>
    public Task<Result<PortfolioView>> SaveAsync(UserId user, PortfolioInput input, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ArgumentNullException.ThrowIfNull(input);
            string name = (input.DisplayName ?? string.Empty).Trim();
            string? headline = Optional(input.Headline);
            string? bio = Optional(input.Bio);
            string visibility = input.Visibility ?? PortfolioVisibility.Private;
            string? slug = Optional(input.PublicSlug)?.ToLowerInvariant();
            if (name.Length is 0 or > MaxDisplayName)
            {
                return Result<PortfolioView>.Failure(Error.Validation("portfolio.display_name", $"A display name of 1 to {MaxDisplayName} characters."));
            }

            if (headline is { Length: > MaxHeadline } || bio is { Length: > MaxBio })
            {
                return Result<PortfolioView>.Failure(Error.Validation("portfolio.text", $"A headline of up to {MaxHeadline} characters and a bio of up to {MaxBio}."));
            }

            if (!PortfolioVisibility.Offered.Contains(visibility))
            {
                return Result<PortfolioView>.Failure(Error.Validation("portfolio.visibility", "private, link_only or public."));
            }

            if (slug is not null && !Slug().IsMatch(slug))
            {
                return Result<PortfolioView>.Failure(Error.Validation("portfolio.slug", "A link name of 3 to 40 lower-case letters, digits and hyphens."));
            }

            if (PortfolioVisibility.IsShown(visibility))
            {
                if (!(await identity.GetCapabilitiesAsync(user, ct).ConfigureAwait(false)).CanPublishShowcase)
                {
                    return Result<PortfolioView>.Failure(Error.Forbidden("portfolio.showcase", "Showing your portfolio outside the platform isn't open to you yet."));
                }

                if (slug is null)
                {
                    return Result<PortfolioView>.Failure(Error.Validation("portfolio.slug", "A shown portfolio needs a link name."));
                }

                // The schema leaves this to the application: nothing shown carries a family name.
                IReadOnlyList<string> surnames = await enrolments.SurnamesOfAsync(user.Value, ct).ConfigureAwait(false);
                if (surnames.Any(s => s.Length >= 2 && Words(name).Contains(s, StringComparer.OrdinalIgnoreCase)))
                {
                    return Result<PortfolioView>.Failure(Error.Validation("portfolio.display_name_legal", "Choose a display name without your family name."));
                }
            }

            DateTimeOffset now = clock.StoredNow();
            LearnerProfileRecord? profile = await db.Profiles.FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
            if (profile is null)
            {
                profile = new LearnerProfileRecord { UserId = user.Value, TenantId = tenant.TenantId.Value, DisplayName = name, Visibility = visibility, CreatedAt = now };
                db.Profiles.Add(profile);
            }

            profile.DisplayName = name;
            profile.Headline = headline;
            profile.Bio = bio;
            profile.Visibility = visibility;
            profile.PublicSlug = slug;
            profile.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Result<PortfolioView>.Failure(Error.Conflict("portfolio.slug_taken", "That link name is taken. Choose another."));
            }

            await projector.TotalAsync(user.Value, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Changed,
                SubjectKind = "learner_profile",
                SubjectIds = [user.Value],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["visibility"] = visibility },
            }, ct).ConfigureAwait(false);
            return Result<PortfolioView>.Success(await MineCoreAsync(user, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>Hides a record from the portfolio, or shows it again. Nothing is deleted.</summary>
    public Task<Result<PortfolioView>> HideAsync(UserId user, Guid participationId, bool hidden, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            ParticipationRecordRecord? record = await db.Participation.FirstOrDefaultAsync(p => p.Id == participationId && p.UserId == user.Value, ct).ConfigureAwait(false);
            if (record is null)
            {
                return Result<PortfolioView>.Failure(Error.NotFound("portfolio.not_found", "Your portfolio has no record like that."));
            }

            record.IsHidden = hidden;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await projector.TotalAsync(user.Value, ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess
            {
                Action = Changed,
                SubjectKind = "participation_record",
                SubjectIds = [record.Id],
                Detail = new Dictionary<string, string>(StringComparer.Ordinal) { ["hidden"] = hidden ? "true" : "false" },
            }, ct).ConfigureAwait(false);
            return Result<PortfolioView>.Success(await MineCoreAsync(user, ct).ConfigureAwait(false));
        }, cancellationToken);

    /// <summary>A portfolio its owner chose to show, by its link name; null for any other.</summary>
    public Task<PublicPortfolio?> PublicAsync(string slug, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync<PublicPortfolio?>(async ct =>
        {
            string name = (slug ?? string.Empty).Trim().ToLowerInvariant();
            if (!Slug().IsMatch(name))
            {
                return null;
            }

            LearnerProfileRecord? profile = await db.Profiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PublicSlug == name && (p.Visibility == PortfolioVisibility.LinkOnly || p.Visibility == PortfolioVisibility.Public), ct).ConfigureAwait(false);
            if (profile is null)
            {
                return null;
            }

            Guid tenantId = tenant.TenantId.Value;
            List<PublicParticipation> shown = await db.Database
                .SqlQuery<PublicParticipation>($"select title as \"Title\", organisation as \"Organisation\", started_on as \"StartedOn\", ended_on as \"EndedOn\", sessions_attended as \"SessionsAttended\", hours as \"Hours\" from learning.public_participation({tenantId}, {name})")
                .ToListAsync(ct).ConfigureAwait(false);
            await audit.RecordInTransactionAsync(db, new AuditAccess { Action = PublicRead, SubjectKind = "learner_profile", SubjectIds = [profile.UserId] }, ct).ConfigureAwait(false);
            return new PublicPortfolio(profile.DisplayName, profile.Headline, profile.Bio, profile.Visibility == PortfolioVisibility.Public, shown.Sum(p => p.Hours), shown);
        }, cancellationToken);

    private async Task<PortfolioView> MineCoreAsync(UserId user, CancellationToken ct)
    {
        LearnerProfileRecord? profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == user.Value, ct).ConfigureAwait(false);
        List<ParticipationRecordRecord> records = await db.Participation.AsNoTracking().Where(p => p.UserId == user.Value)
            .OrderByDescending(p => p.StartedOn).ToListAsync(ct).ConfigureAwait(false);
        bool canShow = (await identity.GetCapabilitiesAsync(user, ct).ConfigureAwait(false)).CanPublishShowcase;
        return new PortfolioView(profile?.DisplayName, profile?.Headline, profile?.Bio, profile?.Visibility ?? PortfolioVisibility.Private, profile?.PublicSlug,
            records.Where(r => !r.IsHidden).Sum(r => r.Hours), canShow, [.. records.Select(View)]);
    }

    private static ParticipationView View(ParticipationRecordRecord r) =>
        new(r.Id, r.TitleSnapshot, r.OrgNameSnapshot, r.StartedOn, r.EndedOn, r.SessionsAttended, r.Hours, r.Source, r.IsHidden);

    private static string? Optional(string? text) => text?.Trim() is { Length: > 0 } t ? t : null;

    private static string[] Words(string text) => Separators().Split(text).Where(w => w.Length > 0).ToArray();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$")]
    private static partial Regex Slug();

    [GeneratedRegex(@"[\s\-'’.,]+")]
    private static partial Regex Separators();
}

public static class LearningModule
{
    /// <summary>The request side: the young person's portfolio. The host registers <see cref="LearningDbContext"/>.</summary>
    public static IServiceCollection AddLearning(this IServiceCollection services)
    {
        services.AddScoped<ParticipationProjector>();
        services.AddScoped<Portfolios>();
        return services;
    }

    /// <summary>The worker's side: participation kept in step with attendance and with the account an enrolment belongs to.</summary>
    public static IServiceCollection AddLearningJobs(this IServiceCollection services)
    {
        services.AddScoped<ParticipationProjector>();
        services.AddMessageHandler<Attendance.Contracts.AttendanceChanged, AttendanceChangedHandler>();
        services.AddMessageHandler<EnrolmentAccountChanged, EnrolmentAccountChangedHandler>();
        return services;
    }
}
