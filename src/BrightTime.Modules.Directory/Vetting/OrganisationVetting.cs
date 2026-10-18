using BrightTime.Modules.Directory.Contracts;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Directory.Vetting;

/// <summary>
/// Who may move an organisation from one vetting state to another, and what
/// each move requires.
/// </summary>
/// <remarks>
/// A state machine rather than a set of endpoint-level checks, because the
/// interesting bugs here are the transitions nobody thought about: approving
/// something that was never submitted, rejecting an organisation that already
/// has published listings, or a provider quietly editing an application that
/// is mid-review.
/// </remarks>
public static class OrganisationVetting
{
    /// <summary>Who is making the move. The same transition is legal for one and not the other.</summary>
    public enum Mover
    {
        /// <summary>Provider staff acting on their own organisation.</summary>
        Provider,

        /// <summary>A network organiser running the tenant.</summary>
        Organiser,
    }

    private static readonly Dictionary<OrganisationStatus, (OrganisationStatus To, Mover By)[]> Allowed =
        new()
        {
            [OrganisationStatus.Draft] =
            [
                (OrganisationStatus.Applied, Mover.Provider),
                (OrganisationStatus.Withdrawn, Mover.Provider),
            ],

            [OrganisationStatus.Applied] =
            [
                (OrganisationStatus.UnderReview, Mover.Organiser),
                (OrganisationStatus.ChangesRequested, Mover.Organiser),
                (OrganisationStatus.Approved, Mover.Organiser),
                (OrganisationStatus.Rejected, Mover.Organiser),
                (OrganisationStatus.Withdrawn, Mover.Provider),
            ],

            [OrganisationStatus.UnderReview] =
            [
                (OrganisationStatus.ChangesRequested, Mover.Organiser),
                (OrganisationStatus.Approved, Mover.Organiser),
                (OrganisationStatus.Rejected, Mover.Organiser),
                (OrganisationStatus.Withdrawn, Mover.Provider),
            ],

            [OrganisationStatus.ChangesRequested] =
            [
                // Resubmission goes back into the queue, not straight to
                // approved: the organiser has to look at what changed.
                (OrganisationStatus.Applied, Mover.Provider),
                (OrganisationStatus.Withdrawn, Mover.Provider),
                (OrganisationStatus.Rejected, Mover.Organiser),
            ],

            [OrganisationStatus.Approved] =
            [
                (OrganisationStatus.Suspended, Mover.Organiser),
                (OrganisationStatus.Withdrawn, Mover.Provider),
            ],

            [OrganisationStatus.Suspended] =
            [
                // Reinstatement is an approval, so it is logged as one.
                (OrganisationStatus.Approved, Mover.Organiser),
                (OrganisationStatus.Rejected, Mover.Organiser),
                (OrganisationStatus.Withdrawn, Mover.Provider),
            ],

            // Terminal. A rejected or withdrawn organisation applies afresh,
            // which gives the new application its own history rather than
            // resurrecting an old decision.
            [OrganisationStatus.Rejected] = [],
            [OrganisationStatus.Withdrawn] = [],
        };

    /// <summary>What a transition requires beyond being structurally legal.</summary>
    public sealed record TransitionContext
    {
        /// <summary>Supplied for rejection, change requests and suspension.</summary>
        public string? Reason { get; init; }

        /// <summary>Whether the organisation has at least one site. Required to approve.</summary>
        public bool HasSite { get; init; }

        /// <summary>Whether an active owner exists. Required to approve; an organisation with nobody in charge cannot be held to anything.</summary>
        public bool HasActiveOwner { get; init; }

        /// <summary>
        /// The organiser confirmed the applicant is who they say: reached the owner on a contact
        /// that the organisation's public record supports. Required to approve; the vetting
        /// checklist records it.
        /// </summary>
        public bool ContactVerified { get; init; }

        /// <summary>Whether a current insurance certificate is on file, where the tenant requires one.</summary>
        public bool HasCurrentInsurance { get; init; }

        public bool TenantRequiresInsurance { get; init; }

        /// <summary>Whether every person with a youth-facing role holds current clearances.</summary>
        public bool YouthFacingStaffCleared { get; init; } = true;
    }

    public static Result CanTransition(
        OrganisationStatus from,
        OrganisationStatus to,
        Mover by,
        TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (from == to)
        {
            return Error.Conflict(
                "organisation.no_change",
                $"The organisation is already {Words(to)}.",
                Args(("status", to)));
        }

        if (!Allowed.TryGetValue(from, out (OrganisationStatus To, Mover By)[]? transitions)
            || transitions.Length == 0)
        {
            return Error.Conflict(
                "organisation.terminal_state",
                $"An organisation that is {Words(from)} cannot change state. It must apply again.",
                Args(("status", from)));
        }

        (OrganisationStatus To, Mover By)[] matching = [.. transitions.Where(t => t.To == to)];

        if (matching.Length == 0)
        {
            return Error.Conflict(
                "organisation.invalid_transition",
                $"An organisation cannot go from {Words(from)} to {Words(to)}.",
                Args(("from", from), ("to", to)));
        }

        if (!matching.Any(t => t.By == by))
        {
            return Error.Forbidden(
                "organisation.wrong_mover",
                by == Mover.Provider
                    ? $"Only a network organiser can move an organisation to {Words(to)}."
                    : $"Only the organisation itself can move to {Words(to)}.",
                Args(("status", to), ("by", by == Mover.Provider ? "provider" : "organiser")));
        }

        return RequirementsFor(to, context);
    }

    private static Result RequirementsFor(OrganisationStatus to, TransitionContext context) => to switch
    {
        OrganisationStatus.Approved => ApprovalRequirements(context),

        OrganisationStatus.Rejected or OrganisationStatus.ChangesRequested or OrganisationStatus.Suspended
            when string.IsNullOrWhiteSpace(context.Reason) =>
            Error.Validation(
                "organisation.reason_required",
                $"Moving an organisation to {Words(to)} needs a reason the provider can read.",
                arguments: Args(("status", to))),

        _ => Result.Success(),
    };

    private static Result ApprovalRequirements(TransitionContext context)
    {
        if (!context.HasActiveOwner)
        {
            return Error.Precondition(
                "organisation.no_owner",
                "An organisation needs an active owner before it can be approved.");
        }

        if (!context.HasSite)
        {
            return Error.Precondition(
                "organisation.no_site",
                "An organisation needs at least one site before it can be approved, " +
                "so that its listings can be found by distance.");
        }

        if (!context.ContactVerified)
        {
            return Error.Precondition(
                "organisation.contact_unverified",
                "Confirm on the vetting checklist that the applicant's contact was verified before approving.");
        }

        if (context.TenantRequiresInsurance && !context.HasCurrentInsurance)
        {
            return Error.Precondition(
                "organisation.insurance_missing",
                "A current certificate of insurance is required before approval.");
        }

        if (!context.YouthFacingStaffCleared)
        {
            return Error.Precondition(
                "organisation.staff_not_cleared",
                "Everyone in a youth-facing role needs current clearances before approval.");
        }

        return Result.Success();
    }

    /// <summary>A status as a phrase, to render in the reader's language: <c>organisation.status.under_review</c>.</summary>
    public static Phrase StatusPhrase(OrganisationStatus status) => Phrase.Of("organisation.status." + status.ToDbValue());

    private static Dictionary<string, object> Args(params ReadOnlySpan<(string Name, object Value)> values)
    {
        Dictionary<string, object> args = new(StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            args[name] = value is OrganisationStatus s ? StatusPhrase(s) : value;
        }

        return args;
    }

    /// <summary>The transitions available right now, for rendering the organiser's action buttons.</summary>
    public static IReadOnlyList<OrganisationStatus> AvailableTransitions(OrganisationStatus from, Mover by) =>
        Allowed.TryGetValue(from, out (OrganisationStatus To, Mover By)[]? transitions)
            ? [.. transitions.Where(t => t.By == by).Select(t => t.To).Distinct()]
            : [];

    /// <summary>A status as a person reads it: "changes requested", not "changes_requested".</summary>
    private static string Words(OrganisationStatus status) => status.ToDbValue().Replace('_', ' ');
}
