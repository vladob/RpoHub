using System.Text.Json;
using RpoHub.Domain;

namespace RpoHub.Application;

public sealed record RemoteFile(string Key, Uri DownloadUri, long? Size, DateTimeOffset? ModifiedAtUtc, string? ETag);
public sealed record ImportReadiness(bool CanInitialize, bool HasRawData, bool HasEtlState, string Explanation);
public sealed record RpoSearchHit(long? Id, string? Identifier, string? FullName, JsonElement Raw);

public interface IRpoApiClient
{
    Task<IReadOnlyList<RpoSearchHit>> SearchByIcoAsync(string ico, CancellationToken cancellationToken);
    Task<JsonDocument> GetEntityAsync(long entityId, bool includeHistory, bool includeUnits, CancellationToken cancellationToken);
}

public interface IRpoExportCatalog
{
    Task<IReadOnlyList<RemoteFile>> ListInitializationFilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteFile>> ListDailyFilesAsync(CancellationToken cancellationToken);
}

public interface IImportStateStore
{
    Task<ImportReadiness> GetReadinessAsync(CancellationToken cancellationToken);
    Task RecordDiscoveredFilesAsync(IEnumerable<RemoteFile> files, string batchKind, CancellationToken cancellationToken);
}

public interface IRawRecordStore
{
    Task StageAsync(SourceRecordKey key, string json, DateTimeOffset? sourceModifiedAtUtc, Guid importFileId, CancellationToken cancellationToken);
}

public sealed class DiscoverRpoUpdates(IRpoExportCatalog catalog, IImportStateStore stateStore)
{
    public async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var files = await catalog.ListDailyFilesAsync(cancellationToken);
        await stateStore.RecordDiscoveredFilesAsync(files, "Daily", cancellationToken);
        return files.Count;
    }
}
