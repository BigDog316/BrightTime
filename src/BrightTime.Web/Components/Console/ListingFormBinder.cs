using BrightTime.Modules.Opportunities.Editing;

namespace BrightTime.Web.Components.Console;

/// <summary>
/// Reads the editor's POST into a <see cref="ListingForm"/>. Field names are the form's
/// property names, so a field and its error message share a key.
/// </summary>
public static class ListingFormBinder
{
    public static ListingForm Read(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);

        string S(string name) => form[name].ToString();
        bool B(string name) => form[name] is { Count: > 0 } v && v.ToString() is "true" or "on";
        List<string> Many(string name) => [.. form[name].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)];
        List<Guid> Ids(string name) => [.. Many(name).Select(v => Guid.TryParse(v, out Guid g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

        return new ListingForm
        {
            Kind = S(nameof(ListingForm.Kind)),
            Title = S(nameof(ListingForm.Title)),
            Summary = S(nameof(ListingForm.Summary)),
            Description = S(nameof(ListingForm.Description)),
            PrimaryLanguage = S(nameof(ListingForm.PrimaryLanguage)),
            AgeMin = S(nameof(ListingForm.AgeMin)),
            AgeMax = S(nameof(ListingForm.AgeMax)),
            GradeMin = S(nameof(ListingForm.GradeMin)),
            GradeMax = S(nameof(ListingForm.GradeMax)),
            TermIds = Ids(nameof(ListingForm.TermIds)),
            AccommodationIds = Ids(nameof(ListingForm.AccommodationIds)),
            Languages = Many(nameof(ListingForm.Languages)),
            Modality = S(nameof(ListingForm.Modality)),
            VirtualUrl = S(nameof(ListingForm.VirtualUrl)),
            SiteIds = Ids(nameof(ListingForm.SiteIds)),
            CostModel = S(nameof(ListingForm.CostModel)),
            CostMin = S(nameof(ListingForm.CostMin)),
            CostMax = S(nameof(ListingForm.CostMax)),
            SlidingScaleNote = S(nameof(ListingForm.SlidingScaleNote)),
            RegistrationMode = S(nameof(ListingForm.RegistrationMode)),
            ExternalUrl = S(nameof(ListingForm.ExternalUrl)),
            Capacity = S(nameof(ListingForm.Capacity)),
            MealsProvided = B(nameof(ListingForm.MealsProvided)),
            TransportProvided = B(nameof(ListingForm.TransportProvided)),
            TransportNote = S(nameof(ListingForm.TransportNote)),
            IsDropIn = B(nameof(ListingForm.IsDropIn)),
            AccessibilityNote = S(nameof(ListingForm.AccessibilityNote)),
            ContactEmail = S(nameof(ListingForm.ContactEmail)),
            ContactPhone = S(nameof(ListingForm.ContactPhone)),
            Repeats = S(nameof(ListingForm.Repeats)),
            Days = Many(nameof(ListingForm.Days)),
            StartsOn = S(nameof(ListingForm.StartsOn)),
            EndsOn = S(nameof(ListingForm.EndsOn)),
            StartTime = S(nameof(ListingForm.StartTime)),
            EndTime = S(nameof(ListingForm.EndTime)),
            DeadlineKind = S(nameof(ListingForm.DeadlineKind)),
            DeadlineOn = S(nameof(ListingForm.DeadlineOn)),
            Version = S(nameof(ListingForm.Version)),
        };
    }
}
