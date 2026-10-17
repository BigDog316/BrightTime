using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Domain;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;
using static BrightTime.Modules.Opportunities.Domain.ListingLifecycle;

namespace BrightTime.Modules.Opportunities.Tests;

public sealed class ListingLifecycleTests
{
    private static readonly TransitionContext Ready = new()
    {
        PassesValidation = true,
        OrganisationApproved = true,
    };

    private static readonly TransitionContext WithReason = new() { Reason = "Please add the registration link." };

    [Fact]
    public void A_provider_submits_and_an_organiser_publishes()
    {
        CanTransition(ListingStatus.Draft, ListingStatus.PendingReview, Mover.Provider, Ready).IsSuccess.ShouldBeTrue();
        CanTransition(ListingStatus.PendingReview, ListingStatus.Published, Mover.Organiser, Ready).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_provider_cannot_publish_its_own_listing_however_it_is_called() =>
        CanTransition(ListingStatus.PendingReview, ListingStatus.Published, Mover.Provider, Ready)
            .Error!.Code.ShouldBe("listing.wrong_mover");

    [Fact]
    public void A_listing_cannot_go_live_while_its_organisation_is_unapproved() =>
        CanTransition(ListingStatus.PendingReview, ListingStatus.Published, Mover.Organiser, Ready with { OrganisationApproved = false })
            .Error!.Code.ShouldBe("listing.organisation_not_approved");

    [Fact]
    public void A_listing_that_fails_validation_cannot_be_submitted_or_published()
    {
        CanTransition(ListingStatus.Draft, ListingStatus.PendingReview, Mover.Provider, Ready with { PassesValidation = false })
            .Error!.Code.ShouldBe("listing.not_ready");

        CanTransition(ListingStatus.PendingReview, ListingStatus.Published, Mover.Organiser, Ready with { PassesValidation = false })
            .Error!.Code.ShouldBe("listing.not_ready");
    }

    [Fact]
    public void Resubmission_re_enters_the_queue_and_does_not_publish_directly() =>
        CanTransition(ListingStatus.ChangesRequested, ListingStatus.Published, Mover.Provider, Ready)
            .Error!.Code.ShouldBe("listing.invalid_transition");

    [Theory]
    [InlineData(ListingStatus.Rejected)]
    [InlineData(ListingStatus.ChangesRequested)]
    [InlineData(ListingStatus.Unpublished)]
    public void Negative_and_withdrawal_moves_need_a_reason(ListingStatus to)
    {
        ListingStatus from = to == ListingStatus.Unpublished ? ListingStatus.Published : ListingStatus.PendingReview;
        const Mover mover = Mover.Organiser;

        CanTransition(from, to, mover, Ready).Error!.Code.ShouldBe("listing.reason_required");
        CanTransition(from, to, mover, WithReason).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_published_listing_can_be_withdrawn_by_its_provider_as_well_as_an_organiser()
    {
        CanTransition(ListingStatus.Published, ListingStatus.Unpublished, Mover.Provider, WithReason).IsSuccess.ShouldBeTrue();
        CanTransition(ListingStatus.Published, ListingStatus.Unpublished, Mover.Organiser, WithReason).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(ListingStatus.Rejected)]
    [InlineData(ListingStatus.Archived)]
    public void Terminal_states_refuse_every_move(ListingStatus from) =>
        CanTransition(from, ListingStatus.Draft, Mover.Provider, Ready)
            .Error!.Code.ShouldBe("listing.terminal_state");

    [Fact]
    public void Moving_to_the_current_state_is_a_conflict() =>
        CanTransition(ListingStatus.Published, ListingStatus.Published, Mover.Organiser, Ready)
            .Error!.Code.ShouldBe("listing.no_change");

    [Fact]
    public void An_unpublished_listing_can_be_restored_by_an_organiser() =>
        CanTransition(ListingStatus.Unpublished, ListingStatus.Published, Mover.Organiser, Ready)
            .IsSuccess.ShouldBeTrue();

    [Fact]
    public void A_provider_can_send_an_unpublished_listing_back_for_review_but_not_republish_it()
    {
        CanTransition(ListingStatus.Unpublished, ListingStatus.PendingReview, Mover.Provider, Ready).IsSuccess.ShouldBeTrue();
        CanTransition(ListingStatus.Unpublished, ListingStatus.Published, Mover.Provider, Ready).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Messages_name_states_in_words_not_database_values()
    {
        Result result = CanTransition(ListingStatus.PendingReview, ListingStatus.ChangesRequested, Mover.Organiser, Ready);

        result.Error!.Message.ShouldContain("changes requested");
        result.Error.Message.ShouldNotContain("_");
    }

    [Fact]
    public void A_status_in_a_message_is_a_phrase_the_reader_sees_in_their_language()
    {
        Result result = CanTransition(ListingStatus.PendingReview, ListingStatus.ChangesRequested, Mover.Organiser, Ready);

        result.Error!.Phrase.Key.ShouldBe("error.listing.reason_required");
        result.Error.Phrase.Arguments["status"].ShouldBe(Phrase.Of("listing.status.changes_requested"));
    }
}
