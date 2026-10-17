using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.Modules.Opportunities.Search;
using BrightTime.SharedKernel.Ids;
using BrightTime.SharedKernel.Primitives;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BrightTime.Web.Tests;

/// <summary>
/// The production composition resolves. Nothing connects: building the readers opens no
/// connection, so this needs no database and catches a missing registration before deploy.
/// </summary>
public sealed class SqlModeCompositionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public SqlModeCompositionTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Finder:Mode", "Sql");
            builder.UseSetting("Finder:ConnectionString", "Host=127.0.0.1;Port=1;Database=none;Username=none");
        });
    }

    [Fact]
    public async Task The_finder_and_the_listing_reader_resolve_to_the_SQL_implementations()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IServiceProvider sp = scope.ServiceProvider;

        sp.GetRequiredService<TenantContext>()
            .SetTenant(new TenantId(Guid.NewGuid()), TimeZoneInfo.Utc, Locale.English);

        sp.GetRequiredService<IListingFinder>().ShouldBeOfType<SqlListingFinder>();
        sp.GetRequiredService<IListingDetails>().ShouldBeOfType<SqlListingDetails>();
    }
}
