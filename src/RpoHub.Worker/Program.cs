using RpoHub.Infrastructure;
using RpoHub.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRpoHubInfrastructure(builder.Configuration);
builder.Services.AddHostedService<UpdateDiscoveryWorker>();
builder.Services.AddHostedService<InitializationImportWorker>();
await builder.Build().RunAsync();
