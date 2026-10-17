using BrightTime.Modules.Opportunities.Editing;
using BrightTime.SharedKernel.Text;

namespace BrightTime.Modules.Opportunities.Import;

/// <summary>
/// One listing read from an import file, before its names and codes are looked up: the form
/// fields as text, and the sites and terms as the file named them.
/// </summary>
public sealed class ImportRecord
{
    /// <summary>The row (CSV: the line; HSDS: the position in the list, from 1).</summary>
    public required int Row { get; init; }

    /// <summary>The provider's own id for the listing, so a second import updates rather than duplicates.</summary>
    public required string ExternalId { get; init; }

    public ListingForm Form { get; } = new();

    public IList<string> SiteNames { get; } = [];

    public IList<string> CategoryCodes { get; } = [];

    public IList<string> InterestCodes { get; } = [];

    public IList<string> AccommodationCodes { get; } = [];

    /// <summary>Problems found while reading the row, keyed by the file's column name.</summary>
    public Dictionary<string, List<Phrase>> Errors { get; } = new(StringComparer.Ordinal);

    public void Fail(string column, Phrase message)
    {
        if (!Errors.TryGetValue(column, out List<Phrase>? list))
        {
            Errors[column] = list = [];
        }

        list.Add(message);
    }
}
