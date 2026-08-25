using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RpoHub.Application;

namespace RpoHub.Infrastructure;

public sealed class RpoInitializationFileImporter(
    HttpClient httpClient,
    string connectionString,
    ILogger<RpoInitializationFileImporter> logger) : IInitializationFileImporter
{
    private const int RawBatchSize = 2_000;

    public async Task<InitializationFileImportResult?> ImportNextStartedBatchAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) [Id]
            FROM [etl].[ImportBatch]
            WHERE [SourceCode] = 'RPO'
              AND [BatchKind] = 'Initialization'
              AND [Status] = 'Started'
            ORDER BY [StartedAtUtc], [Id];
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid batchId ? await ImportNextAsync(batchId, cancellationToken) : null;
    }

    public async Task<InitializationFileImportResult> ImportNextAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var file = await ClaimNextFileAsync(batchId, cancellationToken);
        if (file is null)
            return new InitializationFileImportResult(null, null, 0, 0, "NoPendingFiles", await IsBatchCompleteAsync(batchId, cancellationToken));

        long rowsRead = 0;
        long rowsInserted = 0;
        try
        {
            using var response = await httpClient.GetAsync(file.RemoteUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            ValidateResponseMetadata(file, response.Content.Headers.ContentLength, response.Headers.ETag);

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzipStream = new GZipStream(responseStream, CompressionMode.Decompress, leaveOpen: false);
            using var textReader = new StreamReader(gzipStream, new UTF8Encoding(false, true), true, 1024 * 1024, leaveOpen: false);
            using var jsonReader = new JsonTextReader(textReader)
            {
                CloseInput = false,
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal
            };

            var exportDate = await MoveToResultsArrayAsync(jsonReader, cancellationToken);
            if (exportDate != file.SnapshotDate)
                throw new InvalidDataException($"File export date {exportDate:yyyy-MM-dd} does not match batch snapshot {file.SnapshotDate:yyyy-MM-dd}.");

            await using var sqlConnection = new SqlConnection(connectionString);
            await sqlConnection.OpenAsync(cancellationToken);
            await CreateRawBatchTableAsync(sqlConnection, cancellationToken);
            var batch = new List<RawRecord>(RawBatchSize);
            var reachedEndOfResults = false;

            while (await jsonReader.ReadAsync(cancellationToken))
            {
                if (jsonReader.TokenType == JsonToken.EndArray)
                {
                    reachedEndOfResults = true;
                    break;
                }

                if (jsonReader.TokenType != JsonToken.StartObject)
                    continue;

                var organization = await JObject.LoadAsync(jsonReader, null, cancellationToken);
                var sourceEntityId = organization.Value<long?>("id")
                    ?? throw new InvalidDataException("An organization in the results array has no numeric id.");
                var json = organization.ToString(Formatting.None);
                batch.Add(new RawRecord(sourceEntityId.ToString(CultureInfo.InvariantCulture), json, SHA256.HashData(Encoding.UTF8.GetBytes(json))));
                rowsRead++;

                if (batch.Count < RawBatchSize)
                    continue;

                rowsInserted += await WriteRawBatchAsync(sqlConnection, file.Id, batch, cancellationToken);
                batch.Clear();
                logger.LogInformation("Imported {RowsRead} records from {RemoteKey}.", rowsRead, file.RemoteKey);
            }

            if (!reachedEndOfResults)
                throw new InvalidDataException("The results array ended unexpectedly.");
            if (batch.Count > 0)
                rowsInserted += await WriteRawBatchAsync(sqlConnection, file.Id, batch, cancellationToken);

            var batchCompleted = await CompleteFileAsync(file, rowsRead, cancellationToken);
            logger.LogInformation("Completed {RemoteKey}: {RowsRead} read, {RowsInserted} newly inserted.", file.RemoteKey, rowsRead, rowsInserted);
            return new InitializationFileImportResult(file.Id, file.RemoteKey, rowsRead, rowsInserted, "Imported", batchCompleted);
        }
        catch (Exception exception)
        {
            await FailFileAsync(file.Id, exception, CancellationToken.None);
            logger.LogError(exception, "Import failed for {RemoteKey} after {RowsRead} records.", file.RemoteKey, rowsRead);
            throw;
        }
    }

    private async Task<ImportFileWorkItem?> ClaimNextFileAsync(Guid batchId, CancellationToken cancellationToken)
    {
        const string acquireLockSql = """
            DECLARE @LockResult int;
            EXEC @LockResult = [sys].[sp_getapplock]
                @Resource = N'RpoHub:RPO:InitializationFileClaim',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 0;
            SELECT @LockResult;
            """;
        const string selectSql = """
            SELECT TOP (1)
                [file].[Id], [file].[RemoteKey], [file].[RemoteUri], [file].[SizeBytes], [file].[ETag], [batch].[SnapshotDate]
            FROM [etl].[ImportFile] AS [file] WITH (UPDLOCK, READPAST, ROWLOCK)
            INNER JOIN [etl].[ImportBatch] AS [batch] ON [batch].[Id] = [file].[ImportBatchId]
            WHERE [file].[ImportBatchId] = @BatchId
              AND [batch].[Status] = 'Started'
              AND [file].[BatchKind] = 'Initialization'
              AND [file].[Status] IN ('Discovered', 'Failed')
              AND [file].[AttemptCount] < 3
              AND [file].[RemoteKey] LIKE '%.json.gz'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM [etl].[ImportFile] AS [running] WITH (UPDLOCK, HOLDLOCK)
                  WHERE [running].[ImportBatchId] = @BatchId
                    AND [running].[Status] = 'Importing'
              )
            ORDER BY [file].[SizeBytes], [file].[RemoteKey];
            """;
        const string claimSql = """
            UPDATE [etl].[ImportFile]
            SET [Status] = 'Importing',
                [AttemptCount] = [AttemptCount] + 1,
                [LastAttemptAtUtc] = SYSUTCDATETIME(),
                [ErrorMessage] = NULL
            WHERE [Id] = @FileId
              AND [Status] IN ('Discovered', 'Failed')
              AND [AttemptCount] < 3;
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var lockCommand = new SqlCommand(acquireLockSql, connection, transaction))
        {
            var lockResult = Convert.ToInt32(await lockCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (lockResult < 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using var selectCommand = new SqlCommand(selectSql, connection, transaction);
        selectCommand.Parameters.AddWithValue("@BatchId", batchId);
        ImportFileWorkItem? item = null;
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                item = new ImportFileWorkItem(
                    reader.GetGuid(0),
                    batchId,
                    reader.GetString(1),
                    new Uri(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateOnly.FromDateTime(reader.GetDateTime(5)));
            }
        }

        if (item is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using var claimCommand = new SqlCommand(claimSql, connection, transaction);
        claimCommand.Parameters.AddWithValue("@FileId", item.Id);
        if (await claimCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return item;
    }

    private static async Task<DateOnly> MoveToResultsArrayAsync(JsonTextReader reader, CancellationToken cancellationToken)
    {
        DateOnly? exportDate = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.TokenType != JsonToken.PropertyName)
                continue;

            var propertyName = Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
            if (propertyName == "exportDate")
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.TokenType != JsonToken.String ||
                    !DateOnly.TryParseExact(Convert.ToString(reader.Value, CultureInfo.InvariantCulture), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    throw new InvalidDataException("The exportDate property is missing or invalid.");
                exportDate = parsedDate;
            }
            else if (propertyName == "results")
            {
                if (!await reader.ReadAsync(cancellationToken) || reader.TokenType != JsonToken.StartArray)
                    throw new InvalidDataException("The results property is not a JSON array.");
                return exportDate ?? throw new InvalidDataException("The exportDate property was not found before results.");
            }
        }

        throw new InvalidDataException("The results array was not found.");
    }

    private static void ValidateResponseMetadata(ImportFileWorkItem file, long? contentLength, EntityTagHeaderValue? responseETag)
    {
        if (file.SizeBytes.HasValue && contentLength.HasValue && file.SizeBytes.Value != contentLength.Value)
            throw new InvalidDataException($"Content length {contentLength.Value} does not match catalog size {file.SizeBytes.Value}.");
        if (file.ETag is not null && responseETag is not null && !string.Equals(file.ETag, responseETag.ToString(), StringComparison.Ordinal))
            throw new InvalidDataException($"Response ETag {responseETag} does not match catalog ETag {file.ETag}.");
    }

    private static async Task CreateRawBatchTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE [#RawSourceRecordBatch]
            (
                [SourceEntityId] varchar(100) NOT NULL,
                [JsonData] varchar(max) COLLATE Latin1_General_100_BIN2_UTF8 NOT NULL,
                [ContentHash] binary(32) NOT NULL
            );
            """;
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> WriteRawBatchAsync(SqlConnection connection, Guid fileId, IReadOnlyCollection<RawRecord> records, CancellationToken cancellationToken)
    {
        var table = new DataTable();
        table.Columns.Add("SourceEntityId", typeof(string));
        table.Columns.Add("JsonData", typeof(string));
        table.Columns.Add("ContentHash", typeof(byte[]));
        foreach (var record in records)
            table.Rows.Add(record.SourceEntityId, record.JsonData, record.ContentHash);

        using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null)
        {
            DestinationTableName = "#RawSourceRecordBatch",
            BatchSize = records.Count,
            BulkCopyTimeout = 0
        })
        {
            bulkCopy.ColumnMappings.Add("SourceEntityId", "SourceEntityId");
            bulkCopy.ColumnMappings.Add("JsonData", "JsonData");
            bulkCopy.ColumnMappings.Add("ContentHash", "ContentHash");
            await bulkCopy.WriteToServerAsync(table, cancellationToken);
        }

        const string mergeSql = """
            INSERT INTO [raw].[SourceRecord]
                ([SourceCode], [SourceEntityId], [JsonData], [ContentHash], [SourceModifiedAtUtc], [ImportFileId])
            SELECT
                'RPO', [batch].[SourceEntityId], [batch].[JsonData], [batch].[ContentHash], NULL, @ImportFileId
            FROM [#RawSourceRecordBatch] AS [batch]
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM [raw].[SourceRecord] AS [existing] WITH (UPDLOCK, HOLDLOCK)
                WHERE [existing].[SourceCode] = 'RPO'
                  AND [existing].[SourceEntityId] = [batch].[SourceEntityId]
                  AND [existing].[ContentHash] = [batch].[ContentHash]
            );
            DECLARE @Inserted int = @@ROWCOUNT;
            TRUNCATE TABLE [#RawSourceRecordBatch];
            SELECT @Inserted;
            """;
        await using var command = new SqlCommand(mergeSql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@ImportFileId", fileId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<bool> CompleteFileAsync(ImportFileWorkItem file, long rowCount, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [etl].[ImportFile]
            SET [Status] = 'Imported', [ImportedAtUtc] = SYSUTCDATETIME(), [RowCount] = @RowCount, [ErrorMessage] = NULL
            WHERE [Id] = @FileId AND [Status] = 'Importing';

            DECLARE @Completed bit = 0;
            IF NOT EXISTS
            (
                SELECT 1 FROM [etl].[ImportFile]
                WHERE [ImportBatchId] = @BatchId
                  AND [RemoteKey] LIKE '%.json.gz'
                  AND [Status] <> 'Imported'
            )
            BEGIN
                UPDATE [etl].[ImportBatch]
                SET [Status] = 'Completed', [CompletedAtUtc] = SYSUTCDATETIME(), [ErrorMessage] = NULL
                WHERE [Id] = @BatchId AND [Status] = 'Started';
                SET @Completed = 1;
            END;
            SELECT @Completed;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@FileId", file.Id);
        command.Parameters.AddWithValue("@BatchId", file.BatchId);
        command.Parameters.AddWithValue("@RowCount", rowCount);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task FailFileAsync(Guid fileId, Exception exception, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE [etl].[ImportFile]
            SET [Status] = 'Failed', [ErrorMessage] = LEFT(@ErrorMessage, 4000)
            WHERE [Id] = @FileId;
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@FileId", fileId);
        command.Parameters.AddWithValue("@ErrorMessage", exception.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> IsBatchCompleteAsync(Guid batchId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT CAST(CASE WHEN [Status] = 'Completed' THEN 1 ELSE 0 END AS bit) FROM [etl].[ImportBatch] WHERE [Id] = @BatchId;";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@BatchId", batchId);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private sealed record ImportFileWorkItem(Guid Id, Guid BatchId, string RemoteKey, Uri RemoteUri, long? SizeBytes, string? ETag, DateOnly SnapshotDate);

    private sealed record RawRecord(string SourceEntityId, string JsonData, byte[] ContentHash);
}
