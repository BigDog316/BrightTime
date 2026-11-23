namespace BrightTime.Modules.Opportunities.Contracts;

/// <summary>
/// An email to someone at an organisation that a network organiser has decided on one of its
/// listings (D102): published, changes asked for, rejected, taken down, or a pending edit (D101)
/// approved or sent back. Opportunities books one per person told, with the decision; the host
/// that can word and send mail runs it. Identifiers and codes only: the job table is read across
/// tenants, so the title and the organiser's note are read when the email is sent.
/// </summary>
/// <param name="RecipientUserId">The person told: whoever submitted the listing or saved the edit, or an owner or admin.</param>
/// <param name="OrgId">The listing's organisation.</param>
/// <param name="ListingId">The listing decided on.</param>
/// <param name="Decision">One of <see cref="Decisions"/>.</param>
/// <param name="DecidedAt">When the organiser decided.</param>
public sealed record ListingDecisionNotice(Guid RecipientUserId, Guid OrgId, Guid ListingId, string Decision, DateTimeOffset DecidedAt)
{
    public const string JobType = "opportunity.listing_decision_email";

    public const string Published = "published";
    public const string ChangesRequested = "changes_requested";
    public const string Rejected = "rejected";
    public const string Unpublished = "unpublished";
    public const string EditApproved = "edit_approved";
    public const string EditReturned = "edit_returned";

    public static readonly IReadOnlyList<string> Decisions = [Published, ChangesRequested, Rejected, Unpublished, EditApproved, EditReturned];

    /// <summary>One email per person, listing and decision, however often the decision's job is retried.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string UniqueKey => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{JobType}:{RecipientUserId:N}:{ListingId:N}:{Decision}:{DecidedAt.UtcTicks}");
}

/// <summary>What the email says about the listing.</summary>
/// <param name="Title">The listing's title as families see it.</param>
/// <param name="Note">
/// The organiser's note, while the decision still stands: the reason for changes, a rejection or
/// a take-down, or what to change in a sent-back edit. Null for an approval, or once the listing
/// or the edit has moved on.
/// </param>
public sealed record ListingDecisionFacts(string Title, string? Note);

/// <summary>What the decision email reads about a listing, under the job's tenant.</summary>
public interface IListingDecisions
{
    /// <summary>Null when the listing is gone.</summary>
    Task<ListingDecisionFacts?> GetAsync(Guid listingId, string decision, CancellationToken cancellationToken = default);
}
