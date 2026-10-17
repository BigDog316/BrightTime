namespace BrightTime.Modules.Platform.Domain;

/// <summary>
/// A tenant's own wording for one interface string in one locale, used instead of the shipped
/// catalogue's: a county that says "afterschool" rather than "after-school", or a clearer phrase
/// a community asked for.
/// </summary>
public sealed class ResourceOverride
{
    public Guid TenantId { get; init; }

    public required string Locale { get; init; }

    public required string Key { get; init; }

    public required string Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
