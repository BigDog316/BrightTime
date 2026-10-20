using BrightTime.Infrastructure.Auditing;
using BrightTime.SharedKernel.Ids;

namespace BrightTime.Hosting.Auditing;

/// <summary>
/// The authentication events the audit trail records, and how they are shaped: the account
/// as the subject, the method in the detail, a reason on every refusal.
/// </summary>
/// <remarks>
/// A refused sign-in for an address with no account records no subject and no address: the
/// typed address is not stored anywhere, so the audit log cannot become a list of who tried
/// which emails.
/// </remarks>
public static class AuthAudit
{
    public const string SignIn = "auth.sign_in";
    public const string SignOut = "auth.sign_out";
    public const string ResetRequested = "auth.password_reset.requested";
    public const string ResetCompleted = "auth.password_reset.completed";

    /// <summary>A registration from the app reached an adult account, made for it or already there (detail <c>created</c>).</summary>
    public const string RegistrationRequested = "auth.registration.requested";
    public const string PasskeyAdded = "auth.passkey.added";
    public const string PasskeyRemoved = "auth.passkey.removed";
    public const string ExternalConnected = "auth.external.connected";
    public const string ExternalDisconnected = "auth.external.disconnected";
    public const string StepUp = "auth.step_up";
    public const string SignOutEverywhere = "auth.sign_out_everywhere";

    /// <summary>A spent refresh token was presented again; its token family was revoked.</summary>
    public const string TokenReused = "auth.token.reused";

    public static AuditAccess Allowed(string action, UserId? user, string? method = null, IReadOnlyDictionary<string, string>? detail = null) => new()
    {
        Action = action,
        ActorId = user,
        SubjectKind = user is null ? null : "user",
        SubjectIds = user is { } u ? [u.Value] : [],
        Detail = With(detail, method),
    };

    public static AuditAccess Refused(string action, UserId? account, string reason, string? method = null) => new()
    {
        Action = action,
        Outcome = AuditOutcome.Deny,
        Reason = reason,
        SubjectKind = account is null ? null : "user",
        SubjectIds = account is { } a ? [a.Value] : [],
        Detail = With(null, method),
    };

    private static Dictionary<string, string>? With(IReadOnlyDictionary<string, string>? detail, string? method)
    {
        if (detail is null && method is null)
        {
            return null;
        }

        Dictionary<string, string> values = detail is null ? [] : new(detail);
        if (method is not null)
        {
            values["method"] = method;
        }

        return values;
    }
}
