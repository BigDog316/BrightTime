using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Primitives;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Domain;

/// <summary>
/// Who may move a listing between states, and what each move requires.
/// </summary>
/// <remarks>
/// The same shape as organisation vetting, deliberately. The rule that matters
/// most is the first one: a provider submits and an organiser publishes. A
/// provider cannot make its own listing visible to families, however it is
/// called. Publication also requires an approved organisation, which is checked
/// by the caller and passed in.
/// </remarks>
public static class ListingLifecycle
{
    public enum Mover
    {
        /// <summary>Staff of the owning organisation.</summary>
        Provider,

        /// <summary>A network organiser.</summary>
        Organiser,
    }

    public sealed record TransitionContext
    {
        /// <summary>Required for rejection and change requests, so the provider can read why.</summary>
        public string? Reason { get; init; }

        /// <summary>The owning organisation is approved. Required to publish.</summary>
        public bool OrganisationApproved { get; init; }

        /// <summary>The listing passes <see cref="ListingValidator"/>. Required to submit or publish.</summary>
        public bool PassesValidation { get; init; }
    }

    private static readonly Dictionary<ListingStatus, (ListingStatus To, Mover By)[]> Allowed = new()
    {
        [ListingStatus.Draft] = [(ListingStatus.PendingReview, Mover.Provider), (ListingStatus.Archived, Mover.Provider)],

        [ListingStatus.PendingReview] =
        [
            (ListingStatus.Published, Mover.Organiser),
            (ListingStatus.ChangesRequested, Mover.Organiser),
            (ListingStatus.Rejected, Mover.Organiser),
            (ListingStatus.Draft, Mover.Provider),
        ],

        // Resubmission re-enters the queue; it does not publish directly.
        [ListingStatus.ChangesRequested] =
        [
            (ListingStatus.PendingReview, Mover.Provider),
            (ListingStatus.Archived, Mover.Provider),
        ],

        [ListingStatus.Published] =
        [
            (ListingStatus.Unpublished, Mover.Organiser),
            (ListingStatus.Unpublished, Mover.Provider),
            (ListingStatus.Archived, Mover.Provider),
        ],

        [ListingStatus.Unpublished] =
        [
            (ListingStatus.Published, Mover.Organiser),

            // Back on sale next season, or fixed after an organiser took it down: the provider
            // asks again, and an organiser looks again.
            (ListingStatus.PendingReview, Mover.Provider),
            (ListingStatus.Archived, Mover.Provider),
        ],

        // Terminal. A rejected listing is cloned into a new draft.
        [ListingStatus.Rejected] = [],
        [ListingStatus.Archived] = [],
    };

    public static Result CanTransition(ListingStatus from, ListingStatus to, Mover by, TransitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (from == to)
        {
            return Error.Conflict("listing.no_change", $"The listing is already {Words(to)}.", Args(("status", to)));
        }

        if (!Allowed.TryGetValue(from, out (ListingStatus To, Mover By)[]? transitions) || transitions.Length == 0)
        {
            return Error.Conflict(
                "listing.terminal_state",
                $"A listing that is {Words(from)} cannot change state. Clone it to start again.",
                Args(("status", from)));
        }

        (ListingStatus To, Mover By)[] matching = [.. transitions.Where(t => t.To == to)];
        if (matching.Length == 0)
        {
            return Error.Conflict(
                "listing.invalid_transition",
                $"A listing cannot go from {Words(from)} to {Words(to)}.",
                Args(("from", from), ("to", to)));
        }

        if (!matching.Any(t => t.By == by))
        {
            return Error.Forbidden(
                "listing.wrong_mover",
                by == Mover.Provider
                    ? $"Only a network organiser can move a listing to {Words(to)}."
                    : $"Only the owning organisation can move a listing to {Words(to)}.",
                Args(("status", to), ("by", by == Mover.Provider ? "provider" : "organiser")));
        }

        return RequirementsFor(to, context);
    }

    private static Result RequirementsFor(ListingStatus to, TransitionContext context)
    {
        switch (to)
        {
            case ListingStatus.PendingReview:
                return context.PassesValidation
                    ? Result.Success()
                    : Error.Validation("listing.not_ready", "Fix the highlighted fields before submitting for review.", arguments: Args(("stage", "submit")));

            case ListingStatus.Published:
                if (!context.OrganisationApproved)
                {
                    return Error.Precondition(
                        "listing.organisation_not_approved",
                        "The provider organisation must be approved before its listings can be published.");
                }

                return context.PassesValidation
                    ? Result.Success()
                    : Error.Validation("listing.not_ready", "This listing no longer passes validation and cannot be published.", arguments: Args(("stage", "publish")));

            case ListingStatus.Rejected:
            case ListingStatus.ChangesRequested:
            case ListingStatus.Unpublished:
                return string.IsNullOrWhiteSpace(context.Reason)
                    ? Error.Validation("listing.reason_required", $"Moving a listing to {Words(to)} needs a reason the provider can read.", arguments: Args(("status", to)))
                    : Result.Success();

            default:
                return Result.Success();
        }
    }

    /// <summary>A status as a person reads it: "changes requested", not "changes_requested".</summary>
    private static string Words(ListingStatus status) => status.ToDbValue().Replace('_', ' ');

    /// <summary>A status as a phrase, to render in the reader's language: <c>listing.status.changes_requested</c>.</summary>
    public static Phrase StatusPhrase(ListingStatus status) => Phrase.Of("listing.status." + status.ToDbValue());

    private static Dictionary<string, object> Args(params ReadOnlySpan<(string Name, object Value)> values)
    {
        Dictionary<string, object> args = new(StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            args[name] = value is ListingStatus s ? StatusPhrase(s) : value;
        }

        return args;
    }
}
