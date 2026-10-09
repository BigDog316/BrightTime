namespace BrightTime.Modules.Directory.Contracts;

/// <summary>
/// Where an organisation sits in the vetting workflow.
/// </summary>
/// <remarks>
/// A listing can only be published by an approved organisation, so this state
/// is the gate on everything a provider does. The transitions are deliberately
/// not a free-for-all — see <c>OrganisationVetting</c>.
/// </remarks>
public enum OrganisationStatus
{
    /// <summary>Started but not submitted. Visible only to its own staff.</summary>
    Draft,

    Applied,
    UnderReview,

    /// <summary>The organiser asked for something; the provider can edit and resubmit.</summary>
    ChangesRequested,

    Approved,

    /// <summary>Approved then suspended. Listings are unpublished; the record is kept.</summary>
    Suspended,

    /// <summary>The provider withdrew.</summary>
    Withdrawn,

    Rejected,
}

public static class OrganisationStatusExtensions
{
    /// <summary>Matches <c>ck_organisation_status</c>.</summary>
    public static string ToDbValue(this OrganisationStatus status) => status switch
    {
        OrganisationStatus.Draft => "draft",
        OrganisationStatus.Applied => "applied",
        OrganisationStatus.UnderReview => "under_review",
        OrganisationStatus.ChangesRequested => "changes_requested",
        OrganisationStatus.Approved => "approved",
        OrganisationStatus.Suspended => "suspended",
        OrganisationStatus.Withdrawn => "withdrawn",
        OrganisationStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unmapped organisation status."),
    };

    public static bool TryFromDbValue(string? value, out OrganisationStatus status)
    {
        foreach (OrganisationStatus candidate in Enum.GetValues<OrganisationStatus>())
        {
            if (string.Equals(candidate.ToDbValue(), value, StringComparison.Ordinal))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    /// <summary>Whether this organisation may currently have published listings.</summary>
    public static bool CanPublish(this OrganisationStatus status) => status == OrganisationStatus.Approved;

    /// <summary>Whether the provider can still edit their application.</summary>
    public static bool IsEditableByProvider(this OrganisationStatus status) =>
        status is OrganisationStatus.Draft or OrganisationStatus.ChangesRequested or OrganisationStatus.Approved;
}
