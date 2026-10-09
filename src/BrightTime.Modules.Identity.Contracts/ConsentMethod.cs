namespace BrightTime.Modules.Identity.Contracts;

/// <summary>
/// How a guardian's identity was verified. COPPA permits several; each trades
/// cost and strength against who it excludes.
/// </summary>
public enum ConsentMethod
{
    /// <summary>
    /// COPPA's "email plus": consent by email, followed by a delayed
    /// confirmation. Cheapest and weakest, and the default — but it is only
    /// permitted where the data is not disclosed to third parties, which is
    /// true here.
    /// </summary>
    EmailPlus,

    /// <summary>A small card charge. Strong, but excludes unbanked families.</summary>
    PaymentCardVerification,

    /// <summary>Vendor identity match. Strong, costs money, and excludes undocumented families.</summary>
    GovernmentIdMatch,

    /// <summary>A signed paper form reviewed by staff. The in-person fallback at enrolment.</summary>
    SignedFormReview,

    /// <summary>A video call with trained staff.</summary>
    VideoCallReview,
}

public static class ConsentMethodExtensions
{
    /// <summary>Matches <c>ck_parental_consent_method</c>.</summary>
    public static string ToDbValue(this ConsentMethod method) => method switch
    {
        ConsentMethod.EmailPlus => "email_plus",
        ConsentMethod.PaymentCardVerification => "payment_card_verification",
        ConsentMethod.GovernmentIdMatch => "government_id_match",
        ConsentMethod.SignedFormReview => "signed_form_review",
        ConsentMethod.VideoCallReview => "video_call_review",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unmapped consent method."),
    };
}
