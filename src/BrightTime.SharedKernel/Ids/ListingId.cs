namespace BrightTime.SharedKernel.Ids;

/// <summary>Identifies a listing (a program, event, job or resource). See <see cref="TenantId"/> for why this is wrapped.</summary>
[System.Diagnostics.DebuggerDisplay("Listing {Value}")]
public readonly record struct ListingId(Guid Value)
{
    public static ListingId New() => new(UuidV7.NewId());

    public static readonly ListingId None;

    public bool IsNone => Value == Guid.Empty;

    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
