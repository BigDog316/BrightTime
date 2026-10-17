namespace BrightTime.Web.Auth;

/// <summary>How people sign in to the consoles. See decision D14.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// A login that is a member of <c>brighttime_auth</c>, the only role that may read accounts
    /// before an actor exists. Without it, nobody can sign in and the consoles say so.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Offers "sign in as" a seeded account. Honoured only in the Development environment;
    /// anywhere else the route is not mapped at all.
    /// </summary>
    public bool DevelopmentSignIn { get; set; }

    /// <summary>
    /// The site's own address, for links in email. Required for password reset. Links are
    /// never built from the request's Host header: a forged Host would put a reset token in
    /// a link to someone else's server.
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>
    /// Check new passwords against Have I Been Pwned's range API (T21). Only a five-character
    /// hash prefix leaves the server. Off unless set, because it is an outbound call.
    /// </summary>
    public bool CheckBreachedPasswords { get; set; }

    /// <summary>Sign-in, forgot-password and reset submissions allowed per client address per minute.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;
}
