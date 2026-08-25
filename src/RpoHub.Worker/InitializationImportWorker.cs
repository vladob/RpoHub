using Microsoft.Extensions.Options;
using RpoHub.Application;
using RpoHub.Infrastructure;

namespace RpoHub.Worker;

public sealed class InitializationImportWorker(
    IInitializationFileImporter importer,
    IOptions<RpoOptions> options,
    ILogger<InitializationImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await importer.ImportNextStartedBatchAsync(stoppingToken);
                if (result is null || result.Status == "NoPendingFiles")
                {
                    await Task.Delay(options.Value.InitializationPollInterval, stoppingToken);
                    continue;
                }

                logger.LogInformation(
                    "Initialization file {RemoteKey} completed: {RowsRead} read, {RowsInserted} inserted, batch completed: {BatchCompleted}.",
                    result.RemoteKey,
                    result.RowsRead,
                    result.RowsInserted,
                    result.BatchCompleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Initialization import iteration failed; processing will resume after the polling interval.");
                await Task.Delay(options.Value.InitializationPollInterval, stoppingToken);
            }
        }
    }
}
