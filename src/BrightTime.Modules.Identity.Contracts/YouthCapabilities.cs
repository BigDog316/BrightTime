using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Privacy;

namespace BrightTime.Modules.Identity.Contracts;

/// <summary>
/// What a young person's account is currently allowed to do.
/// </summary>
/// <remarks>
/// Computed from age, consent and verification state, and read by every other
/// module instead of each one re-deriving consent logic. The under-13 floor is
/// unconditional: <c>ck_youth_capability_under13</c> in the schema refuses the
/// row if it is violated, so a bug in the calculator becomes a constraint
/// violation rather than a child posting in public.
/// </remarks>
[YouthData]
public sealed record YouthCapabilities(
    UserId UserId,
    bool IsUnder13,
    bool HasAccountConsent,
    bool CanSelfEnroll,
    bool CanPostInCommunity,
    bool CanUploadMedia,
    bool CanPublishShowcase,
    bool CanReceiveSms,
    bool CanBeMessagedByOrganisation)
{
    /// <summary>The capability set for an adult: everything an account can do, gated only by role.</summary>
    public static YouthCapabilities ForAdult(UserId userId) =>
        new(userId, false, true, true, true, true, true, true, true);

    /// <summary>
    /// The safe default for an account whose state is not yet known. Used when
    /// a lookup fails, so an error degrades to "can do nothing" rather than to
    /// "can do everything".
    /// </summary>
    public static YouthCapabilities Denied(UserId userId) =>
        new(userId, true, false, false, false, false, false, false, false);
}

/// <summary>A capability, named so that an endpoint can declare the one it requires.</summary>
public enum YouthCapability
{
    SelfEnroll,
    PostInCommunity,
    UploadMedia,
    PublishShowcase,
    ReceiveSms,
    BeMessagedByOrganisation,
}

public static class YouthCapabilityExtensions
{
    public static bool IsGrantedBy(this YouthCapability capability, YouthCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capability switch
        {
            YouthCapability.SelfEnroll => capabilities.CanSelfEnroll,
            YouthCapability.PostInCommunity => capabilities.CanPostInCommunity,
            YouthCapability.UploadMedia => capabilities.CanUploadMedia,
            YouthCapability.PublishShowcase => capabilities.CanPublishShowcase,
            YouthCapability.ReceiveSms => capabilities.CanReceiveSms,
            YouthCapability.BeMessagedByOrganisation => capabilities.CanBeMessagedByOrganisation,
            _ => false,
        };
    }
}
