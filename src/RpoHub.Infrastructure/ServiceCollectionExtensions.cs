using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RpoHub.Application;

namespace RpoHub.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRpoHubInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RpoOptions>(configuration.GetSection(RpoOptions.SectionName));
        services.AddHttpClient<IRpoApiClient, RpoApiClient>((provider, client) =>
            client.BaseAddress = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RpoOptions>>().Value.ApiBaseUrl);
        services.AddHttpClient<IRpoExportCatalog, RpoExportCatalog>((provider, client) =>
            client.BaseAddress = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<RpoOptions>>().Value.ExportBaseUrl);
        services.AddHttpClient("RpoPayload", client => client.Timeout = Timeout.InfiniteTimeSpan);

        var connectionString = configuration.GetConnectionString("Registers")
            ?? throw new InvalidOperationException("ConnectionStrings:Registers is required.");
        services.AddSingleton<IImportStateStore>(_ => new SqlImportStateStore(connectionString));
        services.AddSingleton<IRawRecordStore>(_ => new SqlRawRecordStore(connectionString));
        services.AddSingleton<IRpoCoreNormalizer>(_ => new SqlRpoCoreNormalizer(connectionString));
        services.AddTransient<DiscoverRpoUpdates>();
        services.AddTransient<GetRpoInitializationPreview>();
        services.AddTransient<StartRpoInitialization>();
        services.AddTransient<IInitializationFileImporter>(provider =>
            new RpoInitializationFileImporter(
                provider.GetRequiredService<IHttpClientFactory>().CreateClient("RpoPayload"),
                connectionString,
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RpoInitializationFileImporter>>()));
        return services;
    }
}
