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
}
