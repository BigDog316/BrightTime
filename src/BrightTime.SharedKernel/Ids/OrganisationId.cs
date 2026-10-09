namespace BrightTime.SharedKernel.Ids;

/// <summary>Identifies a provider organisation.</summary>
[System.Diagnostics.DebuggerDisplay("Org {Value}")]
public readonly record struct OrganisationId(Guid Value)
{
    public static OrganisationId New() => new(UuidV7.NewId());

    public static readonly OrganisationId None;

    public bool IsNone => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
