using BrightTime.Modules.Enrollment.Contracts;

namespace BrightTime.Modules.Enrollment.Eligibility;

/// <summary>
/// The answer in a host without Learning: nobody has achieved anything, and no badge or playlist
/// exists, so a badge or playlist rule is never met and can't be written (D67).
/// </summary>
internal sealed class NoAchievements : IAchievementQueries
{
    public Task<Achievements> OfAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(Achievements.None);

    public Task<string?> NameAsync(string ruleKind, Guid id, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
}
