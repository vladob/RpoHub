using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RpoHub.Domain;

namespace RpoHub.Application;

public sealed record RemoteFile(string Key, Uri DownloadUri, long? Size, DateTimeOffset? ModifiedAtUtc, string? ETag);
public sealed record ImportReadiness(bool CanInitialize, bool HasRawData, bool HasEtlState, string Explanation);
public sealed record RpoSearchHit(long? Id, string? Identifier, string? FullName, JsonElement Raw);
public sealed record InitializationPackage(
    DateOnly SnapshotDate,
    IReadOnlyList<RemoteFile> Parts,
    RemoteFile? Manifest,
    long TotalCompressedBytes,
    bool HasContiguousParts,
    bool IsStructurallyComplete);
public sealed record InitializationPreview(ImportReadiness Readiness, IReadOnlyList<InitializationPackage> Packages);

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

public sealed partial class GetRpoInitializationPreview(IRpoExportCatalog catalog, IImportStateStore stateStore)
{
    public async Task<InitializationPreview> ExecuteAsync(CancellationToken cancellationToken)
    {
        var readinessTask = stateStore.GetReadinessAsync(cancellationToken);
        var filesTask = catalog.ListInitializationFilesAsync(cancellationToken);
        await Task.WhenAll(readinessTask, filesTask);

        var packages = filesTask.Result
            .Select(ParseInitializationFile)
            .Where(item => item is not null)
            .Select(item => item!.Value)
            .GroupBy(item => item.SnapshotDate)
            .OrderByDescending(group => group.Key)
            .Select(CreatePackage)
            .ToArray();

        return new InitializationPreview(readinessTask.Result, packages);
    }

    private static (DateOnly SnapshotDate, int? PartNumber, bool IsManifest, RemoteFile File)? ParseInitializationFile(RemoteFile file)
    {
        var partMatch = InitializationPartPattern().Match(file.Key);
        if (partMatch.Success &&
            DateOnly.TryParseExact(partMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var partDate) &&
            int.TryParse(partMatch.Groups[2].Value, out var partNumber))
        {
            return (partDate, partNumber, false, file);
        }

        var manifestMatch = InitializationManifestPattern().Match(file.Key);
        if (manifestMatch.Success && DateOnly.TryParseExact(manifestMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var manifestDate))
            return (manifestDate, null, true, file);

        return null;
    }

    private static InitializationPackage CreatePackage(
        IGrouping<DateOnly, (DateOnly SnapshotDate, int? PartNumber, bool IsManifest, RemoteFile File)> group)
    {
        var parts = group
            .Where(item => item.PartNumber.HasValue)
            .OrderBy(item => item.PartNumber)
            .ToArray();
        RemoteFile? manifest = group.Where(item => item.IsManifest).Select(item => item.File).FirstOrDefault();
        var contiguous = parts.Length > 0 && parts.Select((item, index) => item.PartNumber == index + 1).All(value => value);
        return new InitializationPackage(
            group.Key,
            parts.Select(item => item.File).ToArray(),
            manifest,
            parts.Sum(item => item.File.Size ?? 0),
            contiguous,
            manifest is not null && contiguous);
    }

    [GeneratedRegex(@"^batch-init/init_(\d{4}-\d{2}-\d{2})_(\d{3})\.json\.gz$", RegexOptions.CultureInvariant)]
    private static partial Regex InitializationPartPattern();

    [GeneratedRegex(@"^batch-init/init_(\d{4}-\d{2}-\d{2})_list\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex InitializationManifestPattern();
}
