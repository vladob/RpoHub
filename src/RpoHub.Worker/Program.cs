using RpoHub.Infrastructure;
using RpoHub.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddRpoHubInfrastructure(builder.Configuration);
builder.Services.AddHostedService<UpdateDiscoveryWorker>();
builder.Services.AddHostedService<InitializationImportWorker>();
builder.Services.AddHostedService<RpoNormalizationWorker>();
builder.Services.AddHostedService<RpoLegalLifecycleNormalizationWorker>();
await builder.Build().RunAsync();
