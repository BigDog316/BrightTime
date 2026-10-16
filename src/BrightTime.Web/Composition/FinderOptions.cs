using System.ComponentModel.DataAnnotations;

namespace BrightTime.Web.Composition;

/// <summary>
/// How the public finder gets its data and its tenant.
/// </summary>
/// <remarks>
/// <see cref="FinderMode.Sample"/> runs on the fictional Kettle County catalogue and
/// needs no database. It is the default only in development. <see cref="FinderMode.Sql"/>
/// is the production path. Its tenant is set explicitly from configuration until host
/// resolution is wired against a real database.
/// </remarks>
public sealed class FinderOptions
{
    public const string SectionName = "Finder";

    public FinderMode Mode { get; set; } = FinderMode.Sql;

    /// <summary>The tenant the SQL finder serves. Required in <see cref="FinderMode.Sql"/> until host resolution exists.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The tenant's timezone for "starts on" dates.</summary>
    [Required]
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>
    /// The application's login. In <see cref="FinderMode.Sql"/> that login must be granted
    /// membership of <c>brighttime_finder</c> (decision D13): the finder enters that role
    /// with <c>SET LOCAL ROLE</c> for each search, and fails with "permission denied to
    /// set role" if the grant is missing.
    /// </summary>
    public string? ConnectionString { get; set; }
}

public enum FinderMode
{
    Sample,
    Sql,
}
