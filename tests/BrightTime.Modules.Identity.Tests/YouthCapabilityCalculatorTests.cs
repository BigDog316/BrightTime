using BrightTime.Modules.Identity.Consent;
using BrightTime.Modules.Identity.Contracts;
using BrightTime.Modules.Identity.Domain;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Identity.Tests;

/// <summary>
/// The COPPA gate. These are the tests I would least want to be wrong, so they
/// are written as the rules rather than as code paths.
/// </summary>
public sealed class YouthCapabilityCalculatorTests
{
    private static readonly UserId Subject = UserId.New();

    private static CapabilityInputs Child(params ConsentScope[] consents) => new()
    {
        UserId = Subject,
        AccountKind = AccountKind.Child,
        Status = UserStatus.Active,
        AgeBand = AgeBand.Age9To12,
        HasVerifiedGuardian = true,
        LiveConsents = new HashSet<ConsentScope>(consents),
        IsOrganisationVerified = true,
        PhoneConfirmed = true,
        TermsAccepted = true,
    };

    private static CapabilityInputs Teen(params ConsentScope[] consents) => new()
    {
        UserId = Subject,
        AccountKind = AccountKind.Youth,
        Status = UserStatus.Active,
        AgeBand = AgeBand.Age15To17,
        HasVerifiedGuardian = false,
        LiveConsents = new HashSet<ConsentScope>(consents),
        IsOrganisationVerified = true,
        PhoneConfirmed = true,
        TermsAccepted = true,
    };

    // ---- the under-13 floor -------------------------------------------------

    [Fact]
    public void An_under_13_account_cannot_post_publish_or_receive_sms_even_with_every_consent()
    {
        // The floor is not about the guardian's preference. It is about what
        // the platform is willing to collect from, and publish about, a child.
        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(
            Child(Enum.GetValues<ConsentScope>()));

        capabilities.IsUnder13.ShouldBeTrue();
        capabilities.CanPostInCommunity.ShouldBeFalse();
        capabilities.CanPublishShowcase.ShouldBeFalse();
        capabilities.CanReceiveSms.ShouldBeFalse();
        capabilities.CanSelfEnroll.ShouldBeFalse();
    }

    [Fact]
    public void An_under_13_account_can_upload_to_its_own_mentor_with_media_consent()
    {
        // A child submitting a photo of their own work to their own mentor is
        // the point of a playlist. Publication is a separate decision.
        YouthCapabilityCalculator.Calculate(Child(ConsentScope.Account, ConsentScope.MediaCapture))
            .CanUploadMedia.ShouldBeTrue();
    }

    [Fact]
    public void An_under_13_account_cannot_upload_without_media_consent() =>
        YouthCapabilityCalculator.Calculate(Child(ConsentScope.Account))
            .CanUploadMedia.ShouldBeFalse();

    [Fact]
    public void An_under_13_account_with_no_account_consent_can_do_nothing()
    {
        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(
            Child(ConsentScope.MediaCapture, ConsentScope.CommunityParticipation));

        capabilities.HasAccountConsent.ShouldBeFalse();
        capabilities.CanUploadMedia.ShouldBeFalse();
        capabilities.CanBeMessagedByOrganisation.ShouldBeFalse();
    }

    [Fact]
    public void A_child_account_with_no_verified_guardian_can_do_nothing()
    {
        CapabilityInputs inputs = Child(ConsentScope.Account) with { HasVerifiedGuardian = false };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);

        capabilities.HasAccountConsent.ShouldBeFalse();
        capabilities.CanUploadMedia.ShouldBeFalse();
    }

    // ---- unknown age is treated as protected -------------------------------

    [Fact]
    public void An_unknown_age_on_a_youth_account_is_treated_as_under_13()
    {
        // A missing date of birth must not be a route to an unprotected
        // account.
        CapabilityInputs inputs = Teen(Enum.GetValues<ConsentScope>()) with { AgeBand = AgeBand.Unknown };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);

        capabilities.IsUnder13.ShouldBeTrue();
        capabilities.CanPostInCommunity.ShouldBeFalse();
    }

    // ---- teens -------------------------------------------------------------

    [Fact]
    public void A_teen_with_no_guardian_link_consents_for_themselves()
    {
        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(Teen());

        capabilities.IsUnder13.ShouldBeFalse();
        capabilities.HasAccountConsent.ShouldBeTrue();
        capabilities.CanSelfEnroll.ShouldBeTrue();
        capabilities.CanPostInCommunity.ShouldBeTrue();
        capabilities.CanPublishShowcase.ShouldBeTrue();
    }

    [Fact]
    public void A_teen_whose_guardian_manages_the_account_still_needs_the_guardians_consent()
    {
        CapabilityInputs inputs = Teen() with { HasVerifiedGuardian = true };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);

        capabilities.CanPostInCommunity.ShouldBeFalse();
        capabilities.CanPublishShowcase.ShouldBeFalse();
        capabilities.CanSelfEnroll.ShouldBeFalse();
    }

    [Fact]
    public void A_managed_teen_with_the_matching_consents_regains_them()
    {
        CapabilityInputs inputs = Teen(
            ConsentScope.CommunityParticipation,
            ConsentScope.ShowcasePublication,
            ConsentScope.Enrollment) with
        { HasVerifiedGuardian = true };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);

        capabilities.CanPostInCommunity.ShouldBeTrue();
        capabilities.CanPublishShowcase.ShouldBeTrue();
        capabilities.CanSelfEnroll.ShouldBeTrue();
    }

    [Fact]
    public void A_self_registered_teen_cannot_post_upload_or_be_messaged_until_an_organisation_vouches_for_them()
    {
        // The defence against an adult posing as a youth (T2): a self-registered
        // account browses and saves, but cannot reach other young people, or be
        // reached, until enrolment or a district roster verifies it.
        CapabilityInputs inputs = Teen() with { IsOrganisationVerified = false };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);
        capabilities.CanPostInCommunity.ShouldBeFalse();
        capabilities.CanUploadMedia.ShouldBeFalse();
        capabilities.CanBeMessagedByOrganisation.ShouldBeFalse();
        capabilities.CanSelfEnroll.ShouldBeTrue();

        YouthCapabilities vouched = YouthCapabilityCalculator.Calculate(Teen());
        vouched.CanUploadMedia.ShouldBeTrue();
        vouched.CanBeMessagedByOrganisation.ShouldBeTrue();
    }

    [Fact]
    public void A_claimed_teen_uploads_and_is_messaged_on_the_guardians_consent_not_on_vouching()
    {
        // A claimed child profile keeps its guardian (D35); the guardian's consent decides.
        CapabilityInputs managed = Teen(ConsentScope.MediaCapture) with { HasVerifiedGuardian = true, IsOrganisationVerified = false };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(managed);
        capabilities.CanUploadMedia.ShouldBeTrue();
        capabilities.CanBeMessagedByOrganisation.ShouldBeFalse();
    }

    [Fact]
    public void Sms_needs_a_confirmed_phone_number()
    {
        CapabilityInputs inputs = Teen() with { PhoneConfirmed = false };

        YouthCapabilityCalculator.Calculate(inputs).CanReceiveSms.ShouldBeFalse();
    }

    [Fact]
    public void Self_enrolment_needs_the_terms_accepted()
    {
        CapabilityInputs inputs = Teen() with { TermsAccepted = false };

        YouthCapabilityCalculator.Calculate(inputs).CanSelfEnroll.ShouldBeFalse();
    }

    // ---- account state -----------------------------------------------------

    [Theory]
    [InlineData(UserStatus.Suspended)]
    [InlineData(UserStatus.Locked)]
    [InlineData(UserStatus.Deactivated)]
    [InlineData(UserStatus.Deleted)]
    [InlineData(UserStatus.PendingConsent)]
    public void A_non_active_account_can_do_nothing(string status)
    {
        CapabilityInputs inputs = Teen(Enum.GetValues<ConsentScope>()) with { Status = status };

        YouthCapabilities capabilities = YouthCapabilityCalculator.Calculate(inputs);

        capabilities.CanSelfEnroll.ShouldBeFalse();
        capabilities.CanPostInCommunity.ShouldBeFalse();
        capabilities.CanUploadMedia.ShouldBeFalse();
        capabilities.CanReceiveSms.ShouldBeFalse();
    }

    [Fact]
    public void An_active_adult_account_has_everything()
    {
        CapabilityInputs inputs = Teen() with { AccountKind = AccountKind.Adult };

        YouthCapabilityCalculator.Calculate(inputs)
            .ShouldBe(YouthCapabilities.ForAdult(Subject));
    }

    // ---- the floor is re-applied on read -----------------------------------

    [Fact]
    public void A_stored_row_that_violates_the_under_13_floor_is_corrected_on_read()
    {
        // Belt and braces with ck_youth_capability_under13. A row written by a
        // buggy earlier version, or hand-edited in production, must not grant
        // something the floor forbids.
        var tampered = new YouthCapabilityRecord
        {
            UserId = Subject.Value,
            TenantId = Guid.NewGuid(),
            IsUnder13 = true,
            HasAccountConsent = true,
            CanPostInCommunity = true,
            CanPublishShowcase = true,
            CanReceiveSms = true,
            CanSelfEnroll = true,
            CanUploadMedia = true,
        };

        YouthCapabilities capabilities = YouthCapabilityCalculator.FromRecord(tampered);

        capabilities.CanPostInCommunity.ShouldBeFalse();
        capabilities.CanPublishShowcase.ShouldBeFalse();
        capabilities.CanReceiveSms.ShouldBeFalse();
        capabilities.CanSelfEnroll.ShouldBeFalse();
        capabilities.CanUploadMedia.ShouldBeTrue("uploads are permitted under 13 with media consent");
    }

    [Fact]
    public void A_capability_set_round_trips_through_the_persisted_row()
    {
        YouthCapabilities original = YouthCapabilityCalculator.Calculate(Teen());

        var record = new YouthCapabilityRecord { UserId = Subject.Value, TenantId = Guid.NewGuid() };
        YouthCapabilityCalculator.ApplyTo(record, original, DateTimeOffset.UnixEpoch);

        YouthCapabilityCalculator.FromRecord(record).ShouldBe(original);
        record.RecomputedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void The_denied_default_grants_nothing()
    {
        YouthCapabilities denied = YouthCapabilities.Denied(Subject);

        denied.CanSelfEnroll.ShouldBeFalse();
        denied.CanPostInCommunity.ShouldBeFalse();
        denied.CanUploadMedia.ShouldBeFalse();
        denied.CanPublishShowcase.ShouldBeFalse();
        denied.CanReceiveSms.ShouldBeFalse();
        denied.CanBeMessagedByOrganisation.ShouldBeFalse();
        denied.IsUnder13.ShouldBeTrue("an unknown account is assumed protected");
    }

    [Fact]
    public void Every_capability_in_the_enum_is_resolvable()
    {
        YouthCapabilities adult = YouthCapabilities.ForAdult(Subject);

        foreach (YouthCapability capability in Enum.GetValues<YouthCapability>())
        {
            capability.IsGrantedBy(adult).ShouldBeTrue($"{capability} should resolve for an adult");
        }
    }
}
