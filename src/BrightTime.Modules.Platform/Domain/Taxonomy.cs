namespace BrightTime.Modules.Platform.Domain;

/// <summary>A row of <c>platform.taxonomy_term</c>.</summary>
public sealed class TaxonomyTerm
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Vocabulary { get; init; }

    public Guid? ParentId { get; init; }

    public required string Code { get; init; }

    public string? Icon { get; set; }

    public string? Colour { get; set; }

    public short SortOrder { get; set; }

    public bool IsInterest { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>A row of <c>platform.taxonomy_term_name</c>: a term's name in one locale.</summary>
public sealed class TaxonomyTermName
{
    public Guid TermId { get; init; }

    public required string Locale { get; init; }

    public required string Name { get; set; }

    public string? Description { get; set; }
}

/// <summary>A row of <c>platform.neighbourhood_name</c>: a neighbourhood's name in one locale.</summary>
public sealed class NeighbourhoodName
{
    public Guid NeighbourhoodId { get; init; }

    public required string Locale { get; init; }

    public required string Name { get; set; }
}
