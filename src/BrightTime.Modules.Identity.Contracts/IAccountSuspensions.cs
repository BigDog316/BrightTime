using BrightTime.SharedKernel.Ids;

namespace BrightTime.Modules.Identity.Contracts;

/// <summary>
/// Suspending an adult's account for a time (D119), for the county's moderators. Identity keeps
/// the account; the moderation module keeps the decision and its reason.
/// </summary>
public interface IAccountSuspensions
{
    /// <summary>
    /// Suspends the account until <paramref name="until"/> (a longer one already running stays):
    /// it can't sign in or act, its sessions end at their next check, and it becomes active again
    /// by itself when the time comes. False when the account isn't an active or suspended adult's.
    /// </summary>
    Task<bool> SuspendAsync(UserId user, DateTimeOffset until, CancellationToken cancellationToken = default);

    /// <summary>Ends a suspension now. False when the account wasn't suspended.</summary>
    Task<bool> LiftAsync(UserId user, CancellationToken cancellationToken = default);
}

/// <summary>The job a suspension books for when it ends (D119).</summary>
public sealed record SuspensionEnds(Guid UserId, DateTimeOffset Until)
{
    public const string JobType = "identity.suspension_ends";

    /// <summary>One per account and end, however often it's booked.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UniqueKey => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{JobType}:{UserId:N}:{Until.UtcTicks}");
}
