using RpoHub.Infrastructure;
using RpoHub.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRpoHubInfrastructure(builder.Configuration);
builder.Services.AddHostedService<UpdateDiscoveryWorker>();
await builder.Build().RunAsync();
