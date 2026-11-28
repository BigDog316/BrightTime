using BrightTime.Abstractions.Storage;
using BrightTime.Infrastructure.Media;
using BrightTime.Modules.Platform.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Modules.Platform.Media;

/// <summary>Registration of the media library, its store (D103) and its virus scanner (D123). Needs the Platform context.</summary>
public static class MediaModule
{
    public static IServiceCollection AddMediaLibrary(this IServiceCollection services)
    {
        services.AddVirusScanning();
        services.AddScoped<IObjectStore, DatabaseObjectStore>();
        services.AddScoped<IMediaLibrary, MediaLibrary>();
        return services;
    }
}
