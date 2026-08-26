using Microsoft.Extensions.Options;
using RpoHub.Application;
using RpoHub.Infrastructure;

namespace RpoHub.Worker;

public sealed class RpoLegalLifecycleNormalizationWorker(
    IRpoLegalLifecycleNormalizer normalizer,
    IOptions<RpoOptions> options,
    ILogger<RpoLegalLifecycleNormalizationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await normalizer.NormalizeNextBatchAsync(
                    options.Value.NormalizationBatchSize,
                    stoppingToken);

                if (result.LockUnavailable)
                {
                    await Task.Delay(options.Value.NormalizationPollInterval, stoppingToken);
                    continue;
                }

                if (result.NormalizedRecords == 0)
                {
                    await Task.Delay(options.Value.NormalizationPollInterval, stoppingToken);
                    continue;
                }

                logger.LogInformation(
                    "Normalized legal form and lifecycle for {NormalizedRecords} RPO records: {UpsertedLifecycleRecords} lifecycle rows upserted, {InsertedLegalForms} legal forms and {QualityObservations} quality observations inserted.",
                    result.NormalizedRecords,
                    result.UpsertedLifecycleRecords,
                    result.InsertedLegalForms,
                    result.QualityObservations);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RPO legal-form and lifecycle normalization iteration failed; processing will resume after the polling interval.");
                await Task.Delay(options.Value.NormalizationPollInterval, stoppingToken);
            }
        }
    }
}
