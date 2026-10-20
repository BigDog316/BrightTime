using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using BrightTime.Modules.Identity.Contracts;

namespace BrightTime.Modules.Identity.Consent;

/// <summary>How a guardian's link to a child was verified, which bounds what they may consent to.</summary>
public static class VerificationMethod
{
    /// <summary>
    /// Email plus (16 CFR 312.5(b)(2)(vi)): consent by email from a confirmed address, followed
    /// by a confirmatory email. Allowed only where the child's information is not disclosed.
    /// </summary>
    public const string EmailPlus = "email_plus";

    /// <summary>A signed consent form reviewed in person by an organiser (Q3's fallback).</summary>
    public const string SignedFormReview = "signed_form_review";
}

/// <summary>One scope's disclosure: the exact words a guardian is shown, pinned by hash.</summary>
public sealed record ConsentDisclosure(
    ConsentScope Scope,
    string Version,
    string Text,
    byte[] Hash,
    IReadOnlySet<string> Methods,
    bool AvailableUnder13)
{
    public string HashHex => Convert.ToHexStringLower(Hash);
}

/// <summary>
/// What a guardian agrees to, scope by scope, and how sure we must be that they are the guardian.
/// </summary>
/// <remarks>
/// <para>
/// <c>parental_consent.consent_text_hash</c> stores the hash of the text shown, so a later change
/// to the wording can never stand behind an earlier consent. A consent request must name the hash
/// it showed; a stale one is refused, and the guardian is shown the current text.
/// </para>
/// <para>
/// Scopes that disclose a child's information beyond the organisation (publication, community,
/// messaging, data shares, research) need a signed form reviewed in person. Email plus is lawful
/// only for internal use. That rule is for counsel to confirm; see docs/plan/08-decisions.md.
/// </para>
/// <para>
/// <b>The texts are drafts.</b> They need legal review and native-speaker translation before
/// launch (Q13); the version string says so.
/// </para>
/// </remarks>
public static class ConsentDisclosures
{
    public const string DraftVersion = "2026-10-19-draft";

    private static readonly IReadOnlySet<string> Internal =
        new HashSet<string>([VerificationMethod.EmailPlus, VerificationMethod.SignedFormReview], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> Disclosed =
        new HashSet<string>([VerificationMethod.SignedFormReview], StringComparer.Ordinal);

    private static readonly FrozenDictionary<ConsentScope, ConsentDisclosure> ByScope = new[]
    {
        Make(ConsentScope.Account, Internal, true,
            "BrightTime will keep an account for your child, managed by you. It holds their first name, month of birth and the activities you save or sign them up for. Only you and the organisations you choose can see it. You can withdraw this consent at any time, and the account will stop working."),
        Make(ConsentScope.Enrollment, Internal, true,
            "Organisations you apply to will receive the information their application asks for, to decide on a place and run the activity. They may not use it for anything else."),
        Make(ConsentScope.AttendancePhoto, Internal, true,
            "An organisation may take a photo of your child to check them in and out. The photo is used for attendance only and is not published."),
        Make(ConsentScope.MediaCapture, Internal, true,
            "Your child may upload photos or recordings of their own work for their mentors to see. Nothing is published unless you also consent to publication."),
        Make(ConsentScope.LocationUse, Internal, true,
            "The app may use your child's location, only when they ask it to, to find activities nearby. The location is not stored."),
        Make(ConsentScope.Messaging, Disclosed, true,
            "Organisations your child takes part in may send them messages about those activities. You can see every message."),
        Make(ConsentScope.CommunityParticipation, Disclosed, false,
            "Your child may post in community spaces moderated by organisations."),
        Make(ConsentScope.ShowcasePublication, Disclosed, false,
            "Work your child chooses may be shown publicly in the showcase, with their first name."),
        Make(ConsentScope.DistrictDataShare, Disclosed, true,
            "Your child's school district may share attendance and participation information with BrightTime, and receive it back, under its data-sharing agreement."),
        Make(ConsentScope.ResearchUse, Disclosed, true,
            "Information about your child's participation may be used, without their name, in research approved by the county."),
    }.ToFrozenDictionary(d => d.Scope);

    public static IReadOnlyCollection<ConsentDisclosure> All => ByScope.Values;

    public static ConsentDisclosure For(ConsentScope scope) => ByScope[scope];

    private static ConsentDisclosure Make(ConsentScope scope, IReadOnlySet<string> methods, bool availableUnder13, string text) =>
        new(scope, DraftVersion, text,
            SHA256.HashData(Encoding.UTF8.GetBytes(scope.ToDbValue() + "\n" + DraftVersion + "\n" + text)),
            methods, availableUnder13);
}
