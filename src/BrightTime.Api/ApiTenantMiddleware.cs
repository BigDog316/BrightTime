using BrightTime.Hosting.Tenancy;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Platform.Tenancy;
using Microsoft.Extensions.Options;

namespace BrightTime.Api;

/// <summary>The request's tenant, from its host (see <see cref="HostTenancy"/>). Health probes need none.</summary>
public sealed class ApiTenantMiddleware(RequestDelegate next, IOptions<TenancyOptions> tenancy, IConfiguration configuration)
{
    private readonly Uri? _fixedBaseUrl = configuration.GetValue<Uri?>("Auth:PublicBaseUrl");

    public Task InvokeAsync(HttpContext context, TenantContext tenant, SiteContext site, ITenantLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(context);
        return HostTenancy.IsHealthProbe(context.Request)
            ? next(context)
            : HostTenancy.HandleAsync(context, tenant, site, lookup, tenancy.Value, _fixedBaseUrl, next);
    }
}
