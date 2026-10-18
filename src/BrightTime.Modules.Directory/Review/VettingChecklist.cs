using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrightTime.Modules.Directory.Review;

/// <summary>
/// What the organiser checked by hand while vetting. Stored on the vetting event it was saved
/// with (<c>organisation_vetting_event.checklist</c>), so the history says what was checked
/// and when, not only the decision.
/// </summary>
/// <remarks>
/// The insurance expiry is also kept on the organisation, where renewal reminders can find it;
/// the copy here records what was seen at the time.
/// </remarks>
public sealed record VettingChecklist
{
    /// <summary>The owner was reached on a contact the organisation's public record supports. Required to approve.</summary>
    [JsonPropertyName("contact_verified")]
    public bool ContactVerified { get; init; }

    /// <summary>The website and public details match the organisation applied for.</summary>
    [JsonPropertyName("website_checked")]
    public bool WebsiteChecked { get; init; }

    /// <summary>A safeguarding or safety policy was seen.</summary>
    [JsonPropertyName("safety_policy_seen")]
    public bool SafetyPolicySeen { get; init; }

    /// <summary>The expiry on the certificate of insurance seen, if one was.</summary>
    [JsonPropertyName("insurance_expires_on")]
    public DateOnly? InsuranceExpiresOn { get; init; }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    internal string ToJson() => JsonSerializer.Serialize(this, Json);

    internal static VettingChecklist? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VettingChecklist>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The vetting checklist as the organiser sees it: what was ticked, and the facts the
/// platform checks itself.
/// </summary>
/// <param name="Checklist">The latest saved checklist, or an empty one.</param>
/// <param name="SavedAt">When it was saved, or null if never.</param>
/// <param name="HasSite">An open site exists. Required to approve.</param>
/// <param name="ActiveOwners">Active owners. At least one is required to approve.</param>
/// <param name="InsuranceRequired">The tenant requires a current certificate of insurance.</param>
/// <param name="InsuranceExpiresOn">The expiry on file, if any.</param>
/// <param name="InsuranceCurrent">The certificate on file has not expired.</param>
/// <param name="YouthFacingStaff">Active staff and mentors.</param>
/// <param name="WithoutBackgroundCheck">Of those, how many have no current background check recorded. Shown, not enforced, until clearances are recorded in the console.</param>
public sealed record VettingState(
    VettingChecklist Checklist,
    DateTimeOffset? SavedAt,
    bool HasSite,
    int ActiveOwners,
    bool InsuranceRequired,
    DateOnly? InsuranceExpiresOn,
    bool InsuranceCurrent,
    int YouthFacingStaff,
    int WithoutBackgroundCheck);
