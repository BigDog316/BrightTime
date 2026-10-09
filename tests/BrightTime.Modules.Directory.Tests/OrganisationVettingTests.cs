using BrightTime.Modules.Directory.Contracts;
using BrightTime.Modules.Directory.Vetting;
using BrightTime.SharedKernel.Primitives;
using static BrightTime.Modules.Directory.Vetting.OrganisationVetting;

namespace BrightTime.Modules.Directory.Tests;

public sealed class OrganisationVettingTests
{
    private static readonly TransitionContext Approvable = new()
    {
        HasSite = true,
        HasActiveOwner = true,
        HasCurrentInsurance = true,
        TenantRequiresInsurance = true,
        YouthFacingStaffCleared = true,
    };

    private static readonly TransitionContext WithReason = new() { Reason = "Missing insurance certificate." };

    // ---- the happy path ----------------------------------------------------

    [Fact]
    public void A_provider_submits_and_an_organiser_approves()
    {
        CanTransition(OrganisationStatus.Draft, OrganisationStatus.Applied, Mover.Provider, Approvable)
            .IsSuccess.ShouldBeTrue();

        CanTransition(OrganisationStatus.Applied, OrganisationStatus.UnderReview, Mover.Organiser, Approvable)
            .IsSuccess.ShouldBeTrue();

        CanTransition(OrganisationStatus.UnderReview, OrganisationStatus.Approved, Mover.Organiser, Approvable)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_change_request_sends_it_back_for_resubmission_not_straight_to_approved()
    {
        // Resubmission re-enters the queue so the organiser sees what changed.
        CanTransition(OrganisationStatus.ChangesRequested, OrganisationStatus.Applied, Mover.Provider, Approvable)
            .IsSuccess.ShouldBeTrue();

        CanTransition(OrganisationStatus.ChangesRequested, OrganisationStatus.Approved, Mover.Organiser, Approvable)
            .Error!.Code.ShouldBe("organisation.invalid_transition");
    }

    // ---- who may move it ---------------------------------------------------

    [Fact]
    public void A_provider_cannot_approve_itself() =>
        CanTransition(OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Provider, Approvable)
            .Error!.Code.ShouldBe("organisation.wrong_mover");

    [Fact]
    public void An_organiser_cannot_withdraw_a_providers_application() =>
        CanTransition(OrganisationStatus.Applied, OrganisationStatus.Withdrawn, Mover.Organiser, Approvable)
            .Error!.Code.ShouldBe("organisation.wrong_mover");

    [Fact]
    public void A_provider_cannot_suspend_itself_to_dodge_a_review() =>
        CanTransition(OrganisationStatus.Approved, OrganisationStatus.Suspended, Mover.Provider, WithReason)
            .Error!.Code.ShouldBe("organisation.wrong_mover");

    // ---- transitions nobody thinks about ----------------------------------

    [Fact]
    public void An_organisation_cannot_be_approved_without_being_submitted() =>
        CanTransition(OrganisationStatus.Draft, OrganisationStatus.Approved, Mover.Organiser, Approvable)
            .Error!.Code.ShouldBe("organisation.invalid_transition");

    [Theory]
    [InlineData(OrganisationStatus.Rejected)]
    [InlineData(OrganisationStatus.Withdrawn)]
    public void Terminal_states_require_a_fresh_application(OrganisationStatus from) =>
        CanTransition(from, OrganisationStatus.Applied, Mover.Provider, Approvable)
            .Error!.Code.ShouldBe("organisation.terminal_state");

    [Fact]
    public void Moving_to_the_state_it_is_already_in_is_a_conflict_not_a_silent_no_op() =>
        CanTransition(OrganisationStatus.Approved, OrganisationStatus.Approved, Mover.Organiser, Approvable)
            .Error!.Code.ShouldBe("organisation.no_change");

    [Fact]
    public void A_suspended_organisation_can_be_reinstated_and_it_is_logged_as_an_approval() =>
        CanTransition(OrganisationStatus.Suspended, OrganisationStatus.Approved, Mover.Organiser, Approvable)
            .IsSuccess.ShouldBeTrue();

    // ---- approval preconditions -------------------------------------------

    [Fact]
    public void Approval_needs_an_active_owner()
    {
        // An organisation with nobody in charge cannot be held to anything.
        Result result = CanTransition(
            OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Organiser,
            Approvable with { HasActiveOwner = false });

        result.Error!.Code.ShouldBe("organisation.no_owner");
        result.Error.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    public void Approval_needs_at_least_one_site() =>
        CanTransition(
            OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Organiser,
            Approvable with { HasSite = false })
            .Error!.Code.ShouldBe("organisation.no_site");

    [Fact]
    public void Approval_needs_insurance_only_where_the_tenant_requires_it()
    {
        CanTransition(
            OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Organiser,
            Approvable with { HasCurrentInsurance = false })
            .Error!.Code.ShouldBe("organisation.insurance_missing");

        CanTransition(
            OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Organiser,
            Approvable with { HasCurrentInsurance = false, TenantRequiresInsurance = false })
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Approval_needs_youth_facing_staff_cleared() =>
        CanTransition(
            OrganisationStatus.Applied, OrganisationStatus.Approved, Mover.Organiser,
            Approvable with { YouthFacingStaffCleared = false })
            .Error!.Code.ShouldBe("organisation.staff_not_cleared");

    // ---- a decision the provider can read ---------------------------------

    [Theory]
    [InlineData(OrganisationStatus.Rejected)]
    [InlineData(OrganisationStatus.ChangesRequested)]
    public void A_negative_decision_needs_a_reason(OrganisationStatus to)
    {
        Result result = CanTransition(
            OrganisationStatus.UnderReview, to, Mover.Organiser, Approvable);

        result.Error!.Code.ShouldBe("organisation.reason_required");

        CanTransition(OrganisationStatus.UnderReview, to, Mover.Organiser, WithReason)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Suspension_needs_a_reason()
    {
        CanTransition(OrganisationStatus.Approved, OrganisationStatus.Suspended, Mover.Organiser, Approvable)
            .Error!.Code.ShouldBe("organisation.reason_required");

        CanTransition(OrganisationStatus.Approved, OrganisationStatus.Suspended, Mover.Organiser, WithReason)
            .IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reason_is_not_a_reason(string reason) =>
        CanTransition(
            OrganisationStatus.UnderReview, OrganisationStatus.Rejected, Mover.Organiser,
            new TransitionContext { Reason = reason })
            .Error!.Code.ShouldBe("organisation.reason_required");

    // ---- only an approved organisation reaches the public ------------------

    [Theory]
    [InlineData(OrganisationStatus.Approved, true)]
    [InlineData(OrganisationStatus.Suspended, false)]
    [InlineData(OrganisationStatus.Applied, false)]
    [InlineData(OrganisationStatus.ChangesRequested, false)]
    [InlineData(OrganisationStatus.Draft, false)]
    [InlineData(OrganisationStatus.Rejected, false)]
    [InlineData(OrganisationStatus.Withdrawn, false)]
    public void Only_an_approved_organisation_may_publish(OrganisationStatus status, bool expected) =>
        status.CanPublish().ShouldBe(expected);

    [Fact]
    public void Available_transitions_drive_the_organiser_buttons()
    {
        IReadOnlyList<OrganisationStatus> organiser =
            AvailableTransitions(OrganisationStatus.UnderReview, Mover.Organiser);

        organiser.ShouldBe(
            [OrganisationStatus.ChangesRequested, OrganisationStatus.Approved, OrganisationStatus.Rejected],
            ignoreOrder: true);

        AvailableTransitions(OrganisationStatus.Rejected, Mover.Organiser).ShouldBeEmpty();
    }

    [Fact]
    public void Every_status_round_trips_through_its_database_value()
    {
        foreach (OrganisationStatus status in Enum.GetValues<OrganisationStatus>())
        {
            OrganisationStatusExtensions.TryFromDbValue(status.ToDbValue(), out OrganisationStatus parsed)
                .ShouldBeTrue();
            parsed.ShouldBe(status);
        }
    }

    [Fact]
    public void Database_values_match_the_schema_check_constraint()
    {
        // ck_organisation_status lists these exactly; a mismatch is an insert
        // failure at runtime.
        string[] expected =
        [
            "draft", "applied", "under_review", "changes_requested",
            "approved", "suspended", "withdrawn", "rejected",
        ];

        Enum.GetValues<OrganisationStatus>().Select(s => s.ToDbValue()).ShouldBe(expected);
    }
}
