using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Components;
using BrightTime.Web.Composition;
using BrightTime.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddBrightTimeFinder(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Sets the tenant and locale before any page reads them. Runs for every request,
// including the static-asset requests, which then carry the default locale harmlessly.
app.UseMiddleware<TenantRequestMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();

// The listing's "add to calendar" file. A sibling of the page rather than "{slug}.ics", so a
// slug can never be mistaken for a file name. Same visibility rule as the page: 404 otherwise.
app.MapGet("/listings/{slug}/calendar.ics", async (
    string slug,
    HttpContext http,
    IListingDetails details,
    ITenantContext tenant,
    IClock clock,
    CancellationToken ct) =>
{
    ListingDetail? listing = await details.GetPublishedAsync(tenant.TenantId, slug, tenant.Locale.Code, ct);
    if (listing is null)
    {
        return Results.NotFound();
    }

    Uri page = new($"{http.Request.Scheme}://{http.Request.Host}/listings/{Uri.EscapeDataString(listing.Slug)}");
    string ics = ListingCalendar.ToIcs(listing, page, clock.UtcNow);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), ListingCalendar.ContentType, $"{listing.Slug}.ics");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
public partial class Program { }
