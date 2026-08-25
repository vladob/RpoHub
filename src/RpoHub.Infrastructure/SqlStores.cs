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
