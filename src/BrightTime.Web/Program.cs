using BrightTime.Hosting.Health;
using BrightTime.Hosting.Security;
using BrightTime.Infrastructure.Tenancy;
using BrightTime.Modules.Opportunities.Contracts;
using BrightTime.Modules.Opportunities.Details;
using BrightTime.SharedKernel.Time;
using BrightTime.Web.Auth;
using BrightTime.Web.Components;
using BrightTime.Web.Composition;
using BrightTime.Web.Finder;
using BrightTime.Web.Localisation;
using BrightTime.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddClientNetwork(builder.Configuration);
builder.Services.AddBrightTimeFinder(builder.Configuration);
builder.Services.AddBrightTimeTenancy(builder.Configuration, builder.Environment);
builder.Services.AddBrightTimeConsoles(builder.Configuration, builder.Environment);
builder.Services.AddBrightTimeRegistration(builder.Configuration, builder.Environment);
builder.Services.AddBrightTimeAudit(builder.Configuration, builder.Environment,
    hasDatabase: builder.Configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>()?.Mode == FinderMode.Sql);
builder.Services.AddBrightTimeLocalisation(builder.Configuration);
// Cookies, antiforgery and passkey ceremonies, with the key ring in the database when there is one (D39).
builder.Services.AddBrightTimeDataProtection(builder.Configuration, "BrightTime.Web", builder.Environment.IsDevelopment(),
    hasDatabase: builder.Configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>()?.Mode == FinderMode.Sql);
builder.Services.Configure<AppLinksOptions>(builder.Configuration.GetSection(AppLinksOptions.SectionName));
builder.Services.AddBrightTimeHealth(builder.Configuration,
    hasDatabase: builder.Configuration.GetSection(FinderOptions.SectionName).Get<FinderOptions>()?.Mode == FinderMode.Sql);

var app = builder.Build();

// First: everything after it, from HTTPS redirection to the rate limiter, needs the real
// client address and scheme rather than the reverse proxy's.
app.UseClientNetwork();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
// Probes come to the container over plain HTTP; a redirect is not an answer.
app.UseWhen(context => !HostTenancy.IsHealthProbe(context.Request), branch => branch.UseHttpsRedirection());

// Sets the tenant and locale before any page reads them. Runs for every request,
// including the static-asset requests, which then carry the default locale harmlessly.
app.UseMiddleware<TenantRequestMiddleware>();

// After the tenant: the cookie validator rebuilds claims from the tenant's database.
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ActorMiddleware>();

// After the actor, whose own language is one of the choices; before anything renders a word.
app.UseMiddleware<LocaleMiddleware>();
app.UseAuthorization();

// After the actor is known; flushes the request's audit events before its response starts.
app.UseMiddleware<AuditFlushMiddleware>();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapBrightTimeHealth();

// The files that let the app open this site's /app/ links (D35); 404 until an app is configured.
app.MapAppLinks();

// The listing's "add to calendar" file. A sibling of the page rather than "{slug}.ics", so a
// slug can never be mistaken for a file name. Same visibility rule as the page: 404 otherwise.
app.MapGet("/listings/{slug}/calendar.ics", async (
    string slug,
    HttpContext http,
    SiteContext site,
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

    // The tenant's own address, not the request's Host header. Sample data has no tenant
    // address, so it falls back to the request, which is all a development machine has.
    Uri origin = site.BaseUrl ?? new Uri($"{http.Request.Scheme}://{http.Request.Host}/");
    Uri page = new(origin, $"/listings/{Uri.EscapeDataString(listing.Slug)}");
    string ics = ListingCalendar.ToIcs(listing, page, clock.UtcNow);
    return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), ListingCalendar.ContentType, $"{listing.Slug}.ics");
});
app.MapSavedListings();

// The import template is the same for everyone and holds nothing private.
app.MapGet(BrightTime.Web.Components.Console.Provider.ImportPage.TemplatePath, () => Results.File(
    System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(BrightTime.Modules.Opportunities.Import.CsvListingFormat.Template())).ToArray(),
    "text/csv; charset=utf-8",
    "brighttime-listings.csv"));
app.MapBrightTimeSignIn();
app.MapReportDownloads();
if (app.Services.GetRequiredService<ConsoleAvailability>().CanSignIn)
{
    app.MapBrightTimePasskeys();
    app.MapExternalSignIn();
    app.MapStepUp();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
public partial class Program { }
