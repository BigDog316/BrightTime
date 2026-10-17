namespace BrightTime.Web.Auth;

/// <summary>
/// Whether the provider and organiser consoles can run here. They need the database (SQL
/// mode) and a sign-in path; the sample finder has neither, so the pages explain instead of
/// failing.
/// </summary>
public sealed record ConsoleAvailability(bool HasDatabase, bool CanSignIn, bool DevelopmentSignIn)
{
    public bool IsAvailable => HasDatabase && CanSignIn;
}
