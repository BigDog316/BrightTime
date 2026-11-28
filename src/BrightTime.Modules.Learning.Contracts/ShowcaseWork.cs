using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Learning.Contracts;

/// <summary>A young person's project an organisation's staff approved (D69), as the showcase may show it (D124).</summary>
[YouthData]
public sealed record ApprovedProject(Guid Id, Guid AuthorUserId, string Title, string? Description, string? Activity, string? AuthorName, DateTimeOffset ApprovedAt);

/// <summary>
/// The name a young person chose for their portfolio, and whether it carries a family name (D80),
/// for showing their work under it. Null when they haven't chosen one.
/// </summary>
public sealed record ShowcaseName(string? DisplayName, bool CarriesFamilyName);

/// <summary>What Community's showcase (D124) asks Learning: approved projects, and the name to show them under.</summary>
public interface IShowcaseWork
{
    /// <summary>The projects this organisation approved, about its activities, still approved or shown, newest first.</summary>
    Task<IReadOnlyList<ApprovedProject>> ApprovedProjectsAsync(OrganisationId org, CancellationToken cancellationToken = default);

    /// <summary>One of those, or null.</summary>
    Task<ApprovedProject?> ApprovedProjectAsync(OrganisationId org, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The young person's portfolio display name, checked for their family name.</summary>
    Task<ShowcaseName> ShowcaseNameAsync(UserId youth, CancellationToken cancellationToken = default);

    /// <summary>The portfolio display names of these young people, for their organisation's staff; those without one are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> DisplayNamesAsync(IReadOnlyCollection<Guid> youth, CancellationToken cancellationToken = default);
}
