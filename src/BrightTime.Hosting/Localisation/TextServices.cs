using BrightTime.Infrastructure.Tenancy;
using BrightTime.Localisation;
using BrightTime.Modules.Platform.Localisation;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;

namespace BrightTime.Hosting.Localisation;

/// <summary>The request's tenant, as <see cref="Text"/> sees it.</summary>
public sealed class TenantTextContext(ITenantContext tenant) : ITextContext
{
    public TenantId TenantId => tenant.TenantId;

    public bool IsResolved => tenant.IsResolved;

    public Locale Locale => tenant.Locale;

    public TimeZoneInfo TimeZone => tenant.TimeZone;
}

public static class TextServices
{
    /// <summary>
    /// The catalogue, <see cref="Text"/> per request or job scope, and the tenant's wording
    /// overrides: read from <c>platform.resource_override</c> when there is a database, none
    /// otherwise. Needs <see cref="ITenantContext"/>, and with a database the platform context.
    /// </summary>
    public static IServiceCollection AddBrightTimeText(this IServiceCollection services, bool hasDatabase)
    {
        services.AddMemoryCache();
        services.AddSingleton(Catalogue.Load());
        services.AddScoped<ITextContext, TenantTextContext>();
        services.AddScoped<Text>();

        if (hasDatabase)
        {
            services.AddScoped<ResourceOverrides>();
            services.AddScoped<IWordingOverrides, SqlWordingOverrides>();
        }
        else
        {
            services.AddSingleton<IWordingOverrides, NoWordingOverrides>();
        }

        return services;
    }
}
