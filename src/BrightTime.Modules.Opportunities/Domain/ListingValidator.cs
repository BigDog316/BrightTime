using System.Globalization;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Modules.Opportunities.Domain;

/// <summary>
/// The rules a listing must satisfy before it can leave draft.
/// </summary>
/// <remarks>
/// These mirror the CHECK constraints in <c>db/schema/40-opportunity.sql</c>.
/// Validating here means a provider sees a field-level message before a
/// database constraint fires, and it is tested without a database. Parity with
/// the schema is planned as an integration test against the real DDL; until
/// that runs (it needs PostGIS, see the M1 status), the two are kept aligned by
/// review, not by a check.
/// </remarks>
public static class ListingValidator
{
    public const int MaxAge = 30;
    public const int MinGrade = -1;
    public const int MaxGrade = 12;

    public static Result Validate(ListingDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        void Add(string field, string message)
        {
            errors[field] = errors.TryGetValue(field, out string[]? existing)
                ? [.. existing, message]
                : [message];
        }

        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            Add("title", "A listing needs a title.");
        }

        if (string.IsNullOrWhiteSpace(draft.Summary))
        {
            Add("summary", "A listing needs a one-sentence summary.");
        }

        if (draft.AgeMin is { } ageMin && (ageMin < 0 || ageMin > MaxAge))
        {
            Add("ageMin", $"Age must be between 0 and {MaxAge}.");
        }

        if (draft.AgeMax is { } ageMax && (ageMax < 0 || ageMax > MaxAge))
        {
            Add("ageMax", $"Age must be between 0 and {MaxAge}.");
        }

        if (draft.AgeMin is { } lo && draft.AgeMax is { } hi && lo > hi)
        {
            Add("ageMax", "The maximum age cannot be below the minimum.");
        }

        if (draft.GradeMin is { } gMin && (gMin < MinGrade || gMin > MaxGrade))
        {
            Add("gradeMin", "Grade must be between pre-K (-1) and 12.");
        }

        if (draft.GradeMax is { } gMax && (gMax < MinGrade || gMax > MaxGrade))
        {
            Add("gradeMax", "Grade must be between pre-K (-1) and 12.");
        }

        if (draft.GradeMin is { } gLo && draft.GradeMax is { } gHi && gLo > gHi)
        {
            Add("gradeMax", "The highest grade cannot be below the lowest.");
        }

        ValidateCost(draft, Add);
        ValidateRegistration(draft, Add);

        if (draft.Modality != Modality.InPerson && !IsHttpUrl(draft.VirtualUrl))
        {
            Add("virtualUrl", "A virtual or hybrid listing needs a web link to join.");
        }

        if (draft.Kind == ListingKind.Job && draft.CostModel is not (CostModel.Free or CostModel.StipendPaid))
        {
            Add("costModel", "A job is either unpaid or paid through a stipend; choose one of those.");
        }

        if (draft.Kind == ListingKind.Resource && draft.RegistrationMode != RegistrationMode.None)
        {
            Add("registrationMode", "A resource is drop-in: it has no registration.");
        }

        bool hasPlace = draft.SiteCount > 0 || draft.Modality != Modality.InPerson;
        if (!hasPlace)
        {
            Add("sites", "An in-person listing needs at least one site.");
        }

        return errors.Count == 0
            ? Result.Success()
            : Error.Validation("listing.invalid", "Some fields need attention.", errors.AsReadOnly());
    }

    private static void ValidateCost(ListingDraft draft, Action<string, string> add)
    {
        switch (draft.CostModel)
        {
            case CostModel.Free:
                if (draft.CostCentsMin is not null || draft.CostCentsMax is not null)
                {
                    add("costCentsMin", "A free listing has no price.");
                }

                break;

            case CostModel.Fixed:
                if (draft.CostCentsMin is not { } fixedAmount || draft.CostCentsMax is not null)
                {
                    add("costCentsMin", "A fixed-price listing has one price.");
                }
                else if (fixedAmount < 0)
                {
                    add("costCentsMin", "A price cannot be negative.");
                }

                break;

            case CostModel.SlidingScale:
                if (draft.CostCentsMin is not { } low || draft.CostCentsMax is not { } high)
                {
                    add("costCentsMin", "A sliding-scale listing needs both a lowest and a highest price.");
                }
                else if (high < low || low < 0)
                {
                    add("costCentsMax", "The highest price must be at least the lowest, and both must be non-negative.");
                }

                break;
        }
    }

    private static void ValidateRegistration(ListingDraft draft, Action<string, string> add)
    {
        if (draft.RegistrationMode == RegistrationMode.External && !IsHttpUrl(draft.ExternalUrl))
        {
            add("externalUrl", "External registration needs a web link to the provider's registration page.");
        }

        if (draft.RegistrationMode == RegistrationMode.Builtin && draft.CapacityTotal is not > 0)
        {
            add("capacityTotal", "Built-in registration needs a capacity greater than zero.");
        }

        if (draft.CapacityTotal is { } capacity && capacity <= 0)
        {
            add("capacityTotal", "Capacity must be greater than zero.");
        }
    }

    /// <summary>
    /// Accepts only absolute http(s) URLs. Matches <c>~* '^https?://'</c> in the
    /// schema, and rejects anything that parses as a URI but would not be safe
    /// to render as a link (javascript:, data:).
    /// </summary>
    public static bool IsHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host);
    }
}

/// <summary>The fields a provider has entered, in the form the validator and the projector need them.</summary>
public sealed record ListingDraft
{
    public required ListingKind Kind { get; init; }

    public string? Title { get; init; }

    public string? Summary { get; init; }

    public int? AgeMin { get; init; }

    public int? AgeMax { get; init; }

    public int? GradeMin { get; init; }

    public int? GradeMax { get; init; }

    public Modality Modality { get; init; } = Modality.InPerson;

    public string? VirtualUrl { get; init; }

    public CostModel CostModel { get; init; } = CostModel.Free;

    public int? CostCentsMin { get; init; }

    public int? CostCentsMax { get; init; }

    public RegistrationMode RegistrationMode { get; init; } = RegistrationMode.External;

    public string? ExternalUrl { get; init; }

    public int? CapacityTotal { get; init; }

    /// <summary>How many sites the listing is attached to.</summary>
    public int SiteCount { get; init; }
}
