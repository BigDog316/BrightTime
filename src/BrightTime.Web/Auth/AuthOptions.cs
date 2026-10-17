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

    /// <summary>How recently a person must have proved who they are for a step-up action (T21).</summary>
    public int StepUpMinutes { get; set; } = 10;

    /// <summary>Sign-in, forgot-password and reset submissions allowed per client address per minute.</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    /// <summary>
    /// External OpenID Connect providers, keyed by a short name (a-z, 0-9, hyphen) used in
    /// their callback path. Decision D17: they sign in to accounts already connected to them,
    /// and never create or connect one by themselves.
    /// </summary>
    public Dictionary<string, ExternalProviderOptions> External { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>One external OpenID Connect provider.</summary>
public sealed class ExternalProviderOptions
{
    /// <summary>What people see: "Sign in with Google".</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The issuer's https address; its discovery document is read from here.</summary>
    public Uri? Authority { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string? ClientSecret { get; set; }
}
