using Microsoft.Extensions.Options;
using RpoHub.Application;
using RpoHub.Infrastructure;

namespace RpoHub.Worker;

public sealed class UpdateDiscoveryWorker(DiscoverRpoUpdates useCase, IOptions<RpoOptions> options, ILogger<UpdateDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.DiscoveryInterval);
        do
        {
            try
            {
                var count = await useCase.ExecuteAsync(stoppingToken);
                logger.LogInformation("RPO discovery completed; {Count} daily files observed.", count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "RPO discovery failed; the next scheduled attempt remains enabled."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
