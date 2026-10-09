namespace BrightTime.SharedKernel.Ids;

/// <summary>Identifies an account. See <see cref="TenantId"/> for why this is wrapped.</summary>
[System.Diagnostics.DebuggerDisplay("User {Value}")]
public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(UuidV7.NewId());

    public static readonly UserId None;

    public bool IsNone => Value == Guid.Empty;

    public static bool TryParse(string? value, out UserId id)
    {
        if (Guid.TryParse(value, out Guid guid) && guid != Guid.Empty)
        {
            id = new UserId(guid);
            return true;
        }

        id = None;
        return false;
    }

    public override string ToString() => Value.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
}
