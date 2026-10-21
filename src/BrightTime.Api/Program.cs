using BrightTime.Api;
using BrightTime.Api.Applications;
using BrightTime.Api.Auth;
using BrightTime.Api.Consent;
using BrightTime.Api.Decisions;
using BrightTime.Api.Family;
using BrightTime.Api.Forms;
using BrightTime.Api.Me;
using BrightTime.Api.Notifications;
using BrightTime.Api.Public;
using BrightTime.Hosting.Auditing;
using BrightTime.Hosting.Auth;
using BrightTime.Hosting.Health;
using BrightTime.Hosting.Tenancy;

// The API host: the mobile app's surface (docs/plan/05). JSON only, bearer tokens, and the
// same tenancy, audit and row-level security as the web host.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddBrightTimeApi(builder.Configuration, builder.Environment);

WebApplication app = builder.Build();

// First: everything after it needs the real client address and scheme.
app.UseClientNetwork();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Probes come to the container over plain HTTP; a redirect is not an answer.
app.UseWhen(context => !HostTenancy.IsHealthProbe(context.Request), branch => branch.UseHttpsRedirection());

app.UseMiddleware<ApiTenantMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ActorMiddleware>();
// After the actor, whose own language is one of the choices; before anything is written.
app.UseMiddleware<ApiLocaleMiddleware>();
app.UseAuthorization();
app.UseMiddleware<AuditFlushMiddleware>();

app.MapBrightTimeHealth();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/api/v1/openapi.json");
}

RouteGroupBuilder v1 = app.MapGroup("/api/v1");
v1.MapTokenEndpoints();
v1.MapPasskeyEndpoints();
v1.MapExternalEndpoints();
v1.MapRegistrationEndpoints();
v1.MapPasswordEndpoints();
v1.MapMeEndpoints();
v1.MapTermsEndpoints();
v1.MapPublicEndpoints();
v1.MapConsentEndpoints();
v1.MapMeFinderEndpoints();
v1.MapNotificationEndpoints();
v1.MapFamilyEndpoints();
v1.MapFormEndpoints();
v1.MapEligibilityEndpoints();
v1.MapApplicationEndpoints();
v1.MapWaiverEndpoints();
v1.MapDecisionEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>The API host's entry point, named for tests.</summary>
public partial class Program;
