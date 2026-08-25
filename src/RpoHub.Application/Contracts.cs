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
public sealed record InitializationStartResult(
    Guid BatchId,
    DateOnly SnapshotDate,
    int DataFileCount,
    long TotalCompressedBytes,
    string Status);
public sealed record InitializationFileImportResult(
    Guid? FileId,
    string? RemoteKey,
    long RowsRead,
    long RowsInserted,
    string Status,
    bool BatchCompleted);
public sealed record RpoNormalizationBatchResult(
    int NormalizedRecords,
    int InsertedSubjects,
    int InsertedIdentifiers,
    int InsertedNames,
    bool LockUnavailable);

public interface IRpoApiClient
{
    Task<IReadOnlyList<RpoSearchHit>> SearchByIcoAsync(string ico, CancellationToken cancellationToken);
    Task<JsonDocument> GetEntityAsync(long entityId, bool includeHistory, bool includeUnits, CancellationToken cancellationToken);
}

public interface IRpoExportCatalog
{
    Task<IReadOnlyList<RemoteFile>> ListInitializationFilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteFile>> ListDailyFilesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ReadManifestAsync(RemoteFile manifest, CancellationToken cancellationToken);
}

public interface IImportStateStore
{
    Task<ImportReadiness> GetReadinessAsync(CancellationToken cancellationToken);
    Task RecordDiscoveredFilesAsync(IEnumerable<RemoteFile> files, string batchKind, CancellationToken cancellationToken);
    Task<Guid?> TryCreateInitializationBatchAsync(InitializationPackage package, CancellationToken cancellationToken);
}

public interface IRawRecordStore
{
    Task StageAsync(SourceRecordKey key, string json, DateTimeOffset? sourceModifiedAtUtc, Guid importFileId, CancellationToken cancellationToken);
}

public interface IInitializationFileImporter
{
    Task<InitializationFileImportResult> ImportNextAsync(Guid batchId, CancellationToken cancellationToken);
    Task<InitializationFileImportResult?> ImportNextStartedBatchAsync(CancellationToken cancellationToken);
}

public interface IRpoCoreNormalizer
{
    Task<RpoNormalizationBatchResult> NormalizeNextBatchAsync(int batchSize, CancellationToken cancellationToken);
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

public sealed class StartRpoInitialization(
    GetRpoInitializationPreview previewUseCase,
    IRpoExportCatalog catalog,
    IImportStateStore stateStore)
{
    public async Task<InitializationStartResult> ExecuteAsync(DateOnly snapshotDate, CancellationToken cancellationToken)
    {
        var preview = await previewUseCase.ExecuteAsync(cancellationToken);
        if (!preview.Readiness.CanInitialize)
            throw new InvalidOperationException(preview.Readiness.Explanation);

        var package = preview.Packages.SingleOrDefault(item => item.SnapshotDate == snapshotDate)
            ?? throw new InvalidOperationException($"Initialization snapshot {snapshotDate:yyyy-MM-dd} is not available.");
        if (!package.IsStructurallyComplete || package.Manifest is null)
            throw new InvalidOperationException($"Initialization snapshot {snapshotDate:yyyy-MM-dd} is structurally incomplete.");

        var manifestEntries = await catalog.ReadManifestAsync(package.Manifest, cancellationToken);
        var expectedEntries = package.Parts.Select(item => GetFileName(item.Key)).ToArray();
        if (!manifestEntries.SequenceEqual(expectedEntries, StringComparer.Ordinal))
            throw new InvalidOperationException($"Manifest validation failed for initialization snapshot {snapshotDate:yyyy-MM-dd}.");

        var batchId = await stateStore.TryCreateInitializationBatchAsync(package, cancellationToken);
        if (!batchId.HasValue)
            throw new InvalidOperationException("Initialization cannot start because database state changed or another initialization request won the lock.");

        return new InitializationStartResult(
            batchId.Value,
            package.SnapshotDate,
            package.Parts.Count,
            package.TotalCompressedBytes,
            "Started");
    }

    private static string GetFileName(string key)
    {
        var separator = key.LastIndexOf('/');
        return separator < 0 ? key : key[(separator + 1)..];
    }
}
