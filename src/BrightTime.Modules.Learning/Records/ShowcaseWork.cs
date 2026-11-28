using BrightTime.Modules.Enrollment.Contracts;
using BrightTime.Modules.Learning.Contracts;
using BrightTime.Modules.Learning.Persistence;
using BrightTime.Modules.Learning.Portfolio;
using BrightTime.SharedKernel.Ids;
using Microsoft.EntityFrameworkCore;

namespace BrightTime.Modules.Learning.Records;

/// <summary>
/// Learning's answers for the showcase (D124). A project belongs to an organisation's review when
/// it's about one of its activities (a participation record with it), as <see cref="Projects.SubmittedAsync"/>
/// decides; it's approved when staff approved it and nothing has changed it since (a change makes it
/// a draft again, D69). The caller audits what it reads.
/// </summary>
public sealed class ShowcaseWork(LearningDbContext db, IEnrolmentQueries enrolments) : IShowcaseWork
{
    public Task<IReadOnlyList<ApprovedProject>> ApprovedProjectsAsync(OrganisationId org, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(ct => ApprovedAsync(org, null, ct), cancellationToken);

    public async Task<ApprovedProject?> ApprovedProjectAsync(OrganisationId org, Guid projectId, CancellationToken cancellationToken = default) =>
        await db.InTransactionAsync(ct => ApprovedAsync(org, projectId, ct), cancellationToken).ConfigureAwait(false) is [var project, ..] ? project : null;

    public Task<ShowcaseName> ShowcaseNameAsync(UserId youth, CancellationToken cancellationToken = default) =>
        db.InTransactionAsync(async ct =>
        {
            string? name = await db.Profiles.AsNoTracking().Where(p => p.UserId == youth.Value).Select(p => p.DisplayName).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (name?.Trim() is not { Length: > 0 } chosen)
            {
                return new ShowcaseName(null, false);
            }

            IReadOnlyList<string> surnames = await enrolments.SurnamesOfAsync(youth.Value, ct).ConfigureAwait(false);
            return new ShowcaseName(chosen, Portfolios.CarriesFamilyName(chosen, surnames));
        }, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> youth, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(youth);
        Guid[] ids = [.. youth.Distinct()];
        return ids.Length == 0
            ? Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>())
            : db.InTransactionAsync<IReadOnlyDictionary<Guid, string>>(async ct => (await db.Profiles.AsNoTracking()
                    .Where(p => ids.Contains(p.UserId))
                    .Select(p => new { p.UserId, p.DisplayName })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Where(p => p.DisplayName is { Length: > 0 })
                .ToDictionary(p => p.UserId, p => p.DisplayName), cancellationToken);
    }

    private async Task<IReadOnlyList<ApprovedProject>> ApprovedAsync(OrganisationId org, Guid? only, CancellationToken ct)
    {
        IQueryable<ParticipationRecordRecord> ours = db.Participation.Where(r => r.OrgId == org.Value);
        var approved = await (from p in db.Projects.AsNoTracking()
                              where (p.Status == Projects.Approved || p.Status == Projects.Published) && p.ApprovedAt != null
                                    && (only == null || p.Id == only)
                                    && ours.Any(r => r.UserId == p.UserId && r.ListingId == p.ListingId)
                              join lp in db.Profiles on p.UserId equals lp.UserId into profiles
                              from lp in profiles.DefaultIfEmpty()
                              orderby p.ApprovedAt descending
                              select new
                              {
                                  p.Id,
                                  p.UserId,
                                  p.Title,
                                  p.Description,
                                  Activity = ours.Where(r => r.UserId == p.UserId && r.ListingId == p.ListingId).OrderByDescending(r => r.StartedOn).Select(r => r.TitleSnapshot).FirstOrDefault(),
                                  Name = lp == null ? null : lp.DisplayName,
                                  ApprovedAt = p.ApprovedAt!.Value,
                              })
            .Take(500).ToListAsync(ct).ConfigureAwait(false);
        return [.. approved.Select(p => new ApprovedProject(p.Id, p.UserId, p.Title, p.Description, p.Activity, p.Name, p.ApprovedAt))];
    }
}
