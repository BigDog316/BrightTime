using BrightTime.SharedKernel.Messaging;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Learning.Contracts;

/// <summary>A project as a shown portfolio shows it, for a moderator reading a report of the page (D128).</summary>
public sealed record ReportableProject(string Title, string? Description);

/// <summary>
/// A portfolio as anyone with its link sees it, for reporting it and for the moderator who reads
/// the report (D128): the name, headline and bio its owner wrote, its shown projects, and whether
/// it's shown now. Never an account or a legal name.
/// </summary>
[YouthData]
public sealed record ReportablePortfolio(
    Guid UserId, string Slug, string DisplayName, string? Headline, string? Bio, string Visibility, IReadOnlyList<ReportableProject> Projects, DateTimeOffset UpdatedAt)
{
    public bool IsShown => Visibility is "link_only" or "public";
}

/// <summary>What Community's moderation (D128) asks Learning about a shown portfolio.</summary>
public interface IPortfolioModeration
{
    /// <summary>The portfolio shown at this link name, or null when none is.</summary>
    Task<ReportablePortfolio?> ShownAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>The account's portfolio, shown or not, or null when it has none.</summary>
    Task<ReportablePortfolio?> OfAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A moderator took a reported portfolio down (D128): Learning makes it private and tells its
/// owner. Carries the account and the report, nothing else.
/// </summary>
public sealed record PortfolioTakenDown(Guid UserId, Guid ReportId) : IModuleMessage
{
    public static string MessageType => "learning.portfolio.taken_down";
}
