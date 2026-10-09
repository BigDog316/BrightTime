using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The background worker host. It shares every module with the API host but
// exposes no HTTP surface, so a job cannot be triggered from outside.
//
// Job registration, the FOR UPDATE SKIP LOCKED claim loop and the outbox
// dispatcher land here next; for now the host starts and reports healthy so
// the compose stack is complete and the CI pipeline has something to run.
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHostedService<BrightTime.Jobs.JobHostLifetime>();

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
