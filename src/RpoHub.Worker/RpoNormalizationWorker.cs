using Microsoft.Extensions.Options;
using RpoHub.Application;
using RpoHub.Infrastructure;

namespace RpoHub.Worker;

public sealed class RpoNormalizationWorker(
    IRpoCoreNormalizer normalizer,
    IOptions<RpoOptions> options,
    ILogger<RpoNormalizationWorker> logger) : BackgroundService
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
                    "Normalized {NormalizedRecords} RPO records: {InsertedSubjects} subjects, {InsertedIdentifiers} identifiers, {InsertedNames} names inserted.",
                    result.NormalizedRecords,
                    result.InsertedSubjects,
                    result.InsertedIdentifiers,
                    result.InsertedNames);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "RPO normalization iteration failed; processing will resume after the polling interval.");
                await Task.Delay(options.Value.NormalizationPollInterval, stoppingToken);
            }
        }
    }
}
