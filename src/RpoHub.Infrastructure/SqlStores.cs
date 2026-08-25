using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using RpoHub.Application;
using RpoHub.Domain;

namespace RpoHub.Infrastructure;

public sealed class SqlImportStateStore(string connectionString) : IImportStateStore
{
    public async Task<ImportReadiness> GetReadinessAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM raw.SourceRecord) THEN 1 ELSE 0 END AS bit), CAST(CASE WHEN EXISTS(SELECT 1 FROM etl.ImportBatch) THEN 1 ELSE 0 END AS bit);";
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var raw = reader.GetBoolean(0);
        var state = reader.GetBoolean(1);
        var canInitialize = !raw && !state;
        return new ImportReadiness(canInitialize, raw, state, canInitialize ? "Database is empty and ready for initialization." : "Existing raw data or ETL history detected; initialization requires an explicit operator action.");
    }

    public async Task RecordDiscoveredFilesAsync(IEnumerable<RemoteFile> files, string batchKind, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE etl.ImportFile AS target
            USING (SELECT @SourceCode AS SourceCode, @RemoteKey AS RemoteKey) AS source
              ON target.SourceCode = source.SourceCode AND target.RemoteKey = source.RemoteKey
            WHEN MATCHED THEN UPDATE SET RemoteUri=@RemoteUri, SizeBytes=@SizeBytes, ETag=@ETag, SourceModifiedAtUtc=@ModifiedAt
            WHEN NOT MATCHED THEN INSERT (SourceCode, RemoteKey, RemoteUri, BatchKind, Status, SizeBytes, ETag, SourceModifiedAtUtc)
                 VALUES (@SourceCode, @RemoteKey, @RemoteUri, @BatchKind, 'Discovered', @SizeBytes, @ETag, @ModifiedAt);
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var file in files)
        {
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SourceCode", "RPO");
            command.Parameters.AddWithValue("@RemoteKey", file.Key);
            command.Parameters.AddWithValue("@RemoteUri", file.DownloadUri.ToString());
            command.Parameters.AddWithValue("@BatchKind", batchKind);
            command.Parameters.AddWithValue("@SizeBytes", (object?)file.Size ?? DBNull.Value);
            command.Parameters.AddWithValue("@ETag", (object?)file.ETag ?? DBNull.Value);
            command.Parameters.AddWithValue("@ModifiedAt", (object?)file.ModifiedAtUtc ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<Guid?> TryCreateInitializationBatchAsync(InitializationPackage package, CancellationToken cancellationToken)
    {
        const string createBatchSql = """
            DECLARE @LockResult int;
            EXEC @LockResult = sys.sp_getapplock
                @Resource = N'RpoHub:RPO:Initialization',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 0;

            IF @LockResult < 0
            BEGIN
                SELECT CAST(NULL AS uniqueidentifier);
                RETURN;
            END;

            IF EXISTS (SELECT 1 FROM [raw].[SourceRecord] WITH (UPDLOCK, HOLDLOCK))
               OR EXISTS (SELECT 1 FROM [etl].[ImportBatch] WITH (UPDLOCK, HOLDLOCK))
            BEGIN
                SELECT CAST(NULL AS uniqueidentifier);
                RETURN;
            END;

            INSERT INTO [etl].[ImportBatch]
                ([SourceCode], [BatchKind], [SnapshotDate], [Status])
            OUTPUT INSERTED.[Id]
            VALUES
                ('RPO', 'Initialization', @SnapshotDate, 'Started');
            """;

        const string registerFileSql = """
            MERGE [etl].[ImportFile] WITH (HOLDLOCK) AS [target]
            USING (SELECT @SourceCode AS [SourceCode], @RemoteKey AS [RemoteKey]) AS [source]
               ON [target].[SourceCode] = [source].[SourceCode]
              AND [target].[RemoteKey] = [source].[RemoteKey]
            WHEN MATCHED THEN
                UPDATE SET
                    [ImportBatchId] = @ImportBatchId,
                    [RemoteUri] = @RemoteUri,
                    [BatchKind] = 'Initialization',
                    [Status] = @Status,
                    [SizeBytes] = @SizeBytes,
                    [ETag] = @ETag,
                    [SourceModifiedAtUtc] = @ModifiedAt
            WHEN NOT MATCHED THEN
                INSERT ([ImportBatchId], [SourceCode], [RemoteKey], [RemoteUri], [BatchKind], [Status], [SizeBytes], [ETag], [SourceModifiedAtUtc])
                VALUES (@ImportBatchId, @SourceCode, @RemoteKey, @RemoteUri, 'Initialization', @Status, @SizeBytes, @ETag, @ModifiedAt);
            """;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using var createCommand = new SqlCommand(createBatchSql, connection, transaction);
            createCommand.Parameters.AddWithValue("@SnapshotDate", package.SnapshotDate.ToDateTime(TimeOnly.MinValue));
            var scalar = await createCommand.ExecuteScalarAsync(cancellationToken);
            if (scalar is not Guid batchId)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            foreach (var file in package.Parts.Append(package.Manifest!))
            {
                await using var command = new SqlCommand(registerFileSql, connection, transaction);
                command.Parameters.AddWithValue("@ImportBatchId", batchId);
                command.Parameters.AddWithValue("@SourceCode", "RPO");
                command.Parameters.AddWithValue("@RemoteKey", file.Key);
                command.Parameters.AddWithValue("@RemoteUri", file.DownloadUri.ToString());
                command.Parameters.AddWithValue("@Status", file.Key == package.Manifest!.Key ? "Downloaded" : "Discovered");
                command.Parameters.AddWithValue("@SizeBytes", (object?)file.Size ?? DBNull.Value);
                command.Parameters.AddWithValue("@ETag", (object?)file.ETag ?? DBNull.Value);
                command.Parameters.AddWithValue("@ModifiedAt", (object?)file.ModifiedAtUtc ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return batchId;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}

public sealed class SqlRawRecordStore(string connectionString) : IRawRecordStore
{
    public async Task StageAsync(SourceRecordKey key, string json, DateTimeOffset? sourceModifiedAtUtc, Guid importFileId, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT raw.SourceRecord (SourceCode, SourceEntityId, JsonData, ContentHash, SourceModifiedAtUtc, ImportFileId)
            VALUES (@SourceCode, @SourceEntityId, @JsonData, @ContentHash, @SourceModifiedAtUtc, @ImportFileId);
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@SourceCode", key.SourceCode);
        command.Parameters.AddWithValue("@SourceEntityId", key.SourceEntityId);
        command.Parameters.AddWithValue("@JsonData", json);
        command.Parameters.AddWithValue("@ContentHash", SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        command.Parameters.AddWithValue("@SourceModifiedAtUtc", (object?)sourceModifiedAtUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("@ImportFileId", importFileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class SqlRpoCoreNormalizer(string connectionString) : IRpoCoreNormalizer
{
    public async Task<RpoNormalizationBatchResult> NormalizeNextBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("[etl].[NormalizeRpoCoreBatch]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
            CommandTimeout = 300
        };
        command.Parameters.Add("@BatchSize", System.Data.SqlDbType.Int).Value = batchSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new RpoNormalizationBatchResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetBoolean(4));
    }
}
