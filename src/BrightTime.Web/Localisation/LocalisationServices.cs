using System.Text.Encodings.Web;
using System.Text.Unicode;
using BrightTime.Hosting.Localisation;
using BrightTime.Web.Composition;

namespace BrightTime.Web.Localisation;

public static class LocalisationServices
{
    public static IServiceCollection AddBrightTimeLocalisation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Write every script as itself. The default encoder allows only Basic Latin and turns each
        // Arabic, Bengali or Chinese letter into a numeric entity: correct, and several times the
        // size, on a site meant to work on a 2G connection. Markup characters are still encoded.
        services.AddWebEncoders(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

        FinderOptions finder = configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>() ?? new();
        return services.AddBrightTimeText(hasDatabase: finder.Mode == FinderMode.Sql);
    }
}
