using System.Globalization;
using BrightTime.Modules.Directory.Provider;

namespace BrightTime.Web.Components.Console;

/// <summary>Reads the provider pages' POSTs into the Directory module's forms. Field names are the forms' property names.</summary>
public static class ProviderFormBinder
{
    /// <summary>Hour rows on the site form: Hours[n].Day and so on, for n from 0.</summary>
    public const int HourRows = 7;

    public static OrganisationForm Organisation(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);
        string S(string name) => form[name].ToString();
        return new OrganisationForm
        {
            DisplayName = S(nameof(OrganisationForm.DisplayName)),
            LegalName = S(nameof(OrganisationForm.LegalName)),
            Kind = S(nameof(OrganisationForm.Kind)),
            WebsiteUrl = S(nameof(OrganisationForm.WebsiteUrl)),
            Mission = S(nameof(OrganisationForm.Mission)),
            PublicEmail = S(nameof(OrganisationForm.PublicEmail)),
            PublicPhone = S(nameof(OrganisationForm.PublicPhone)),
            Languages = [.. form[nameof(OrganisationForm.Languages)].Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!)],
        };
    }

    /// <param name="form">The POST.</param>
    /// <param name="prefix">"Site." when the site is part of a larger form (the application), otherwise empty.</param>
    public static SiteForm Site(IFormCollection form, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(form);
        string S(string name) => form[prefix + name].ToString();
        List<SiteHoursForm> hours = [];
        for (int i = 0; i < HourRows; i++)
        {
            string row = string.Create(CultureInfo.InvariantCulture, $"Hours[{i}].");
            hours.Add(new SiteHoursForm(S(row + "Day"), S(row + "Opens"), S(row + "Closes"), S(row + "Season")));
        }

        return new SiteForm
        {
            Name = S(nameof(SiteForm.Name)),
            AddressLine1 = S(nameof(SiteForm.AddressLine1)),
            AddressLine2 = S(nameof(SiteForm.AddressLine2)),
            Locality = S(nameof(SiteForm.Locality)),
            PostalCode = S(nameof(SiteForm.PostalCode)),
            NeighbourhoodId = S(nameof(SiteForm.NeighbourhoodId)),
            IsAddressPublic = S(nameof(SiteForm.IsAddressPublic)) is "true" or "on",
            EntranceNote = S(nameof(SiteForm.EntranceNote)),
            TransitNote = S(nameof(SiteForm.TransitNote)),
            ParkingNote = S(nameof(SiteForm.ParkingNote)),
            Wheelchair = S(nameof(SiteForm.Wheelchair)),
            Restroom = S(nameof(SiteForm.Restroom)),
            Elevator = S(nameof(SiteForm.Elevator)),
            QuietSpace = S(nameof(SiteForm.QuietSpace)),
            Hours = hours,
        };
    }
}
