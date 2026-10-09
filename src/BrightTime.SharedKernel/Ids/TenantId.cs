namespace BrightTime.SharedKernel.Ids;

/// <summary>
/// Identifies a tenant (a region deployment). Wrapped rather than passed as a
/// bare <see cref="Guid"/> because it is threaded through every query, every
/// RLS session variable and every audit record: a transposed argument between
/// two <see cref="Guid"/> parameters is a cross-tenant bug, and the compiler
/// should catch it.
/// </summary>
[System.Diagnostics.DebuggerDisplay("Tenant {Value}")]
public readonly record struct TenantId(Guid Value)
{
    public static TenantId New() => new(UuidV7.NewId());

    public static readonly TenantId None;

    public bool IsNone => Value == Guid.Empty;

    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out TenantId id)
    {
        if (Guid.TryParse(value, out Guid guid) && guid != Guid.Empty)
        {
            id = new TenantId(guid);
            return true;
        }

        id = None;
        return false;
    }

    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
