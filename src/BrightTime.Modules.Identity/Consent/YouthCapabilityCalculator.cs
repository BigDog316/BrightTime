using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Domain;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>What the calculator needs to know about an account, so it can be tested without a database.</summary>
public sealed record CapabilityInputs
{
    public required UserId UserId { get; init; }

    public required string AccountKind { get; init; }

    public required string Status { get; init; }

    public AgeBand AgeBand { get; init; } = SharedKernel.Primitives.AgeBand.Unknown;

    /// <summary>Verified, unrevoked guardian link exists. Required for any child-account capability.</summary>
    public bool HasVerifiedGuardian { get; init; }

    /// <summary>The scopes with a live, unexpired, unrevoked consent row.</summary>
    public IReadOnlySet<ConsentScope> LiveConsents { get; init; } = new HashSet<ConsentScope>();

    /// <summary>
    /// The account has been vouched for by an organisation, through enrolment
    /// or a district roster. Self-registered youth start without this.
    /// </summary>
    public bool IsOrganisationVerified { get; init; }

    public bool PhoneConfirmed { get; init; }

    public bool TermsAccepted { get; init; }
}

/// <summary>
/// Turns age, consent and verification state into a capability set.
/// </summary>
/// <remarks>
/// <para>
/// The whole COPPA gate is this one class, deliberately: twelve modules
/// re-deriving "may this child post?" is how a platform ends up with eleven
/// correct answers and one wrong one.
/// </para>
/// <para>
/// Three rules it will not bend:
/// </para>
/// <list type="number">
/// <item>
/// An under-13 account never posts, never publishes to the showcase and never
/// receives SMS, whatever consent rows exist. A guardian cannot consent their
/// way past the floor, because the floor is not about the guardian's
/// preference — it is about what we are willing to collect from and publish
/// about a child.
/// </item>
/// <item>
/// An unknown age is treated as under 13. A missing date of birth must not be
/// a route to an unprotected account.
/// </item>
/// <item>
/// Absence of a consent row is a no. There is no implied grant and no
/// grandfathering.
/// </item>
/// </list>
/// </remarks>
public static class YouthCapabilityCalculator
{
    public static YouthCapabilities Calculate(CapabilityInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.AccountKind == Domain.AccountKind.Adult)
        {
            return inputs.Status == UserStatus.Active
                ? YouthCapabilities.ForAdult(inputs.UserId)
                : YouthCapabilities.Denied(inputs.UserId);
        }

        bool isUnder13 = inputs.AccountKind == Domain.AccountKind.Child
                         || inputs.AgeBand.IsCoppaProtected();

        // A suspended, locked or deleted account does nothing, and neither
        // does a child account with no verified guardian behind it.
        bool accountUsable = inputs.Status == UserStatus.Active
                             && (!isUnder13 || inputs.HasVerifiedGuardian);

        if (!accountUsable)
        {
            return YouthCapabilities.Denied(inputs.UserId) with { IsUnder13 = isUnder13 };
        }

        bool hasAccountConsent = !isUnder13 || Consented(inputs, ConsentScope.Account);

        if (isUnder13 && !hasAccountConsent)
        {
            // enforce_child_consent() in the schema refuses to let this account
            // be active at all; this is the application saying the same thing.
            return YouthCapabilities.Denied(inputs.UserId) with { IsUnder13 = true };
        }

        bool canSelfEnroll = !isUnder13
                             && inputs.TermsAccepted
                             && (!RequiresConsent(inputs) || Consented(inputs, ConsentScope.Enrollment));

        bool canPostInCommunity = !isUnder13
                                  && inputs.IsOrganisationVerified
                                  && (!RequiresConsent(inputs) || Consented(inputs, ConsentScope.CommunityParticipation));

        // Uploads are the one capability an under-13 account can hold, because
        // a child submitting a photo of their own work to their own mentor is
        // the point of a playlist. It needs explicit media-capture consent, and
        // publication is a separate decision below. A teen consenting for
        // themselves uploads once an organisation has vouched for them (T2).
        bool canUploadMedia = isUnder13 || RequiresConsent(inputs)
            ? Consented(inputs, ConsentScope.MediaCapture)
            : inputs.IsOrganisationVerified;

        bool canPublishShowcase = !isUnder13
                                  && (!RequiresConsent(inputs) || Consented(inputs, ConsentScope.ShowcasePublication));

        bool canReceiveSms = !isUnder13
                             && inputs.PhoneConfirmed
                             && (!RequiresConsent(inputs) || Consented(inputs, ConsentScope.Messaging));

        // T2 again: an organisation messages a self-registered teen only once one has vouched for them.
        bool canBeMessagedByOrg = isUnder13 || RequiresConsent(inputs)
            ? Consented(inputs, ConsentScope.Messaging)
            : inputs.IsOrganisationVerified;

        return new YouthCapabilities(
            inputs.UserId,
            isUnder13,
            hasAccountConsent,
            canSelfEnroll,
            canPostInCommunity,
            canUploadMedia,
            canPublishShowcase,
            canReceiveSms,
            canBeMessagedByOrg);
    }

    /// <summary>
    /// A 13+ account that a guardian manages still needs the guardian's
    /// consent for the scoped activities; a 13+ account with no guardian link
    /// consents for itself.
    /// </summary>
    private static bool RequiresConsent(CapabilityInputs inputs) =>
        inputs.AccountKind == Domain.AccountKind.Child || inputs.HasVerifiedGuardian;

    private static bool Consented(CapabilityInputs inputs, ConsentScope scope) =>
        inputs.LiveConsents.Contains(scope);

    /// <summary>
    /// Maps the persisted row back to the contract type. Re-applies the
    /// under-13 floor on read as well as on write: a row that predates a
    /// calculator fix, or one hand-edited in production, cannot grant something
    /// the floor forbids.
    /// </summary>
    public static YouthCapabilities FromRecord(YouthCapabilityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool under13 = record.IsUnder13;

        return new YouthCapabilities(
            new UserId(record.UserId),
            under13,
            record.HasAccountConsent,
            record.CanSelfEnroll && !under13,
            record.CanPostInCommunity && !under13,
            record.CanUploadMedia,
            record.CanPublishShowcase && !under13,
            record.CanReceiveSms && !under13,
            record.CanBeMessagedByOrg);
    }

    public static void ApplyTo(YouthCapabilityRecord record, YouthCapabilities capabilities, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(capabilities);

        record.IsUnder13 = capabilities.IsUnder13;
        record.HasAccountConsent = capabilities.HasAccountConsent;
        record.CanSelfEnroll = capabilities.CanSelfEnroll;
        record.CanPostInCommunity = capabilities.CanPostInCommunity;
        record.CanUploadMedia = capabilities.CanUploadMedia;
        record.CanPublishShowcase = capabilities.CanPublishShowcase;
        record.CanReceiveSms = capabilities.CanReceiveSms;
        record.CanBeMessagedByOrg = capabilities.CanBeMessagedByOrganisation;
        record.RecomputedAt = at;
    }
}
