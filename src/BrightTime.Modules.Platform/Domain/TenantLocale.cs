namespace BrightTime.Modules.Platform.Domain;

/// <summary>A locale a tenant publishes in, with its text-search configuration and writing direction.</summary>
public sealed class TenantLocale
{
    public Guid TenantId { get; init; }

    public required string Locale { get; init; }

    public bool IsDefault { get; set; }

    public bool IsRtl { get; set; }

    /// <summary>PostgreSQL <c>regconfig</c> name. Stored as text, not <c>regconfig</c>: an OID would silently repoint if a configuration were recreated.</summary>
    public string TsConfig { get; set; } = SharedKernel.Primitives.Locale.TextSearchSimple;

    public short SortOrder { get; set; }
}
