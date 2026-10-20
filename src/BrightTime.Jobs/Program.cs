using BrightTime.Jobs;

// The background worker host. It shares every module with the API host but exposes no HTTP
// surface, so a job cannot be triggered from outside.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddBrightTimeWorker(builder.Configuration, builder.Environment);

// Fail at startup, not on the first job, if a handler's dependencies are not registered.
builder.Services.Configure<ServiceProviderOptions>(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
