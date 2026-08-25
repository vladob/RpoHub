namespace RpoHub.Infrastructure;

public sealed class RpoOptions
{
    public const string SectionName = "Rpo";
    public Uri ApiBaseUrl { get; set; } = new("https://api.statistics.sk/rpo/v1/");
    public Uri ExportBaseUrl { get; set; } = new("https://frkqbrydxwdp.compat.objectstorage.eu-frankfurt-1.oraclecloud.com/susr-rpo/");
    public string InitializationPrefix { get; set; } = "batch-init/";
    public string DailyPrefix { get; set; } = "batch-daily/";
    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan InitializationPollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public int NormalizationBatchSize { get; set; } = 5000;
    public TimeSpan NormalizationPollInterval { get; set; } = TimeSpan.FromSeconds(30);
}
