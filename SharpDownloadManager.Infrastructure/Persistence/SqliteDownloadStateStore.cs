using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.Infrastructure.Persistence;

public sealed class SqliteDownloadStateStore : IDownloadStateStore
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public SqliteDownloadStateStore(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dataFolder = Path.Combine(baseFolder, "SharpDownloadManager");
        Directory.CreateDirectory(dataFolder);

        _dbPath = Path.Combine(dataFolder, "downloads.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var createDownloads = @"
CREATE TABLE IF NOT EXISTS Downloads (
    Id TEXT PRIMARY KEY,
    Url TEXT NOT NULL,
    FileName TEXT NOT NULL,
    SavePath TEXT NOT NULL,
    TempFolderPath TEXT NOT NULL,
    Status INTEGER NOT NULL,
    Mode INTEGER NOT NULL,
    ContentLength INTEGER NULL,
    SupportsRange INTEGER NOT NULL,
    ETag TEXT NULL,
    LastModified TEXT NULL,
    TotalDownloadedBytes INTEGER NOT NULL,
    BytesWritten INTEGER NOT NULL DEFAULT 0,
    ConnectionsCount INTEGER NOT NULL,
    LastErrorCode INTEGER NOT NULL,
    LastErrorMessage TEXT NULL,
    RequestHeaders TEXT NULL
);";

            var createChunks = @"
CREATE TABLE IF NOT EXISTS Chunks (
    DownloadId TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL,
    StartByte INTEGER NOT NULL,
    EndByte INTEGER NOT NULL,
    DownloadedBytes INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    RetryCount INTEGER NOT NULL,
    LastErrorCode INTEGER NULL,
    LastErrorMessage TEXT NULL,
    PRIMARY KEY (DownloadId, ChunkIndex),
    FOREIGN KEY (DownloadId) REFERENCES Downloads(Id) ON DELETE CASCADE
);";

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = createDownloads;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = createChunks;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "BytesWritten",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestHeaders",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Info("SQLite state store initialized successfully.", eventCode: "STATE_STORE_INIT", context: new { DbPath = _dbPath });
        }
        catch (SqliteException ex) when (IsCorruptionError(ex))
        {
            _logger.Error("SQLite state store appears corrupted; attempting reset.", eventCode: "STATE_STORE_CORRUPTED", exception: ex, context: new { DbPath = _dbPath });

            TryArchiveCorruptedDb();

            // Retry once on a fresh DB
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Downloads (
    Id TEXT PRIMARY KEY,
    Url TEXT NOT NULL,
    FileName TEXT NOT NULL,
    SavePath TEXT NOT NULL,
    TempFolderPath TEXT NOT NULL,
    Status INTEGER NOT NULL,
    Mode INTEGER NOT NULL,
    ContentLength INTEGER NULL,
    SupportsRange INTEGER NOT NULL,
    ETag TEXT NULL,
    LastModified TEXT NULL,
    TotalDownloadedBytes INTEGER NOT NULL,
    BytesWritten INTEGER NOT NULL DEFAULT 0,
    ConnectionsCount INTEGER NOT NULL,
    LastErrorCode INTEGER NOT NULL,
    LastErrorMessage TEXT NULL,
    RequestHeaders TEXT NULL
);";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Chunks (
    DownloadId TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL,
    StartByte INTEGER NOT NULL,
    EndByte INTEGER NOT NULL,
    DownloadedBytes INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    RetryCount INTEGER NOT NULL,
    LastErrorCode INTEGER NULL,
    LastErrorMessage TEXT NULL,
    PRIMARY KEY (DownloadId, ChunkIndex),
    FOREIGN KEY (DownloadId) REFERENCES Downloads(Id) ON DELETE CASCADE
);";
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "BytesWritten",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestHeaders",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Info("SQLite state store recreated after corruption.", eventCode: "STATE_STORE_RESET", context: new { DbPath = _dbPath });
        }
    }

    public async Task SaveDownloadAsync(DownloadTask task, CancellationToken cancellationToken = default)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        try
        {
            // Upsert into Downloads
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
INSERT INTO Downloads (
    Id, Url, FileName, SavePath, TempFolderPath,
    Status, Mode, ContentLength, SupportsRange,
    ETag, LastModified,
    TotalDownloadedBytes, BytesWritten, ConnectionsCount,
    LastErrorCode, LastErrorMessage,
    RequestHeaders
) VALUES (
    $Id, $Url, $FileName, $SavePath, $TempFolderPath,
    $Status, $Mode, $ContentLength, $SupportsRange,
    $ETag, $LastModified,
    $TotalDownloadedBytes, $BytesWritten, $ConnectionsCount,
    $LastErrorCode, $LastErrorMessage,
    $RequestHeaders
)
ON CONFLICT(Id) DO UPDATE SET
    Url = excluded.Url,
    FileName = excluded.FileName,
    SavePath = excluded.SavePath,
    TempFolderPath = excluded.TempFolderPath,
    Status = excluded.Status,
    Mode = excluded.Mode,
    ContentLength = excluded.ContentLength,
    SupportsRange = excluded.SupportsRange,
    ETag = excluded.ETag,
    LastModified = excluded.LastModified,
    TotalDownloadedBytes = excluded.TotalDownloadedBytes,
    BytesWritten = excluded.BytesWritten,
    ConnectionsCount = excluded.ConnectionsCount,
    LastErrorCode = excluded.LastErrorCode,
    LastErrorMessage = excluded.LastErrorMessage,
    RequestHeaders = excluded.RequestHeaders;
";

                cmd.Parameters.AddWithValue("$Id", task.Id.ToString());
                cmd.Parameters.AddWithValue("$Url", task.Url);
                cmd.Parameters.AddWithValue("$FileName", task.FileName);
                cmd.Parameters.AddWithValue("$SavePath", task.SavePath);
                cmd.Parameters.AddWithValue("$TempFolderPath", task.TempFolderPath);
                cmd.Parameters.AddWithValue("$Status", (int)task.Status);
                cmd.Parameters.AddWithValue("$Mode", (int)task.Mode);
                if (task.ContentLength.HasValue)
                    cmd.Parameters.AddWithValue("$ContentLength", task.ContentLength.Value);
                else
                    cmd.Parameters.AddWithValue("$ContentLength", DBNull.Value);
                cmd.Parameters.AddWithValue("$SupportsRange", task.SupportsRange ? 1 : 0);
                cmd.Parameters.AddWithValue("$ETag", (object?)task.ETag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$LastModified", task.LastModified?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$TotalDownloadedBytes", task.TotalDownloadedBytes);
                cmd.Parameters.AddWithValue("$BytesWritten", task.BytesWritten);
                cmd.Parameters.AddWithValue("$ConnectionsCount", task.ConnectionsCount);
                cmd.Parameters.AddWithValue("$LastErrorCode", (int)task.LastErrorCode);
                cmd.Parameters.AddWithValue("$LastErrorMessage", (object?)task.LastErrorMessage ?? DBNull.Value);
                var headersJson = SerializeHeaders(task.RequestHeaders);
                cmd.Parameters.AddWithValue("$RequestHeaders", (object?)headersJson ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Remove existing chunks for this task
            using (var deleteChunks = connection.CreateCommand())
            {
                deleteChunks.Transaction = transaction;
                deleteChunks.CommandText = "DELETE FROM Chunks WHERE DownloadId = $DownloadId;";
                deleteChunks.Parameters.AddWithValue("$DownloadId", task.Id.ToString());
                await deleteChunks.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // Insert current chunks
            if (task.Chunks is not null)
            {
                foreach (var chunk in task.Chunks)
                {
                    using var insertChunk = connection.CreateCommand();
                    insertChunk.Transaction = transaction;
                    insertChunk.CommandText = @"
INSERT INTO Chunks (
    DownloadId, ChunkIndex, StartByte, EndByte,
    DownloadedBytes, Status, RetryCount,
    LastErrorCode, LastErrorMessage
) VALUES (
    $DownloadId, $Index, $StartByte, $EndByte,
    $DownloadedBytes, $Status, $RetryCount,
    $LastErrorCode, $LastErrorMessage
);";
                    insertChunk.Parameters.AddWithValue("$DownloadId", task.Id.ToString());
                    insertChunk.Parameters.AddWithValue("$Index", chunk.Index);
                    insertChunk.Parameters.AddWithValue("$StartByte", chunk.StartByte);
                    insertChunk.Parameters.AddWithValue("$EndByte", chunk.EndByte);
                    insertChunk.Parameters.AddWithValue("$DownloadedBytes", chunk.DownloadedBytes);
                    insertChunk.Parameters.AddWithValue("$Status", (int)chunk.Status);
                    insertChunk.Parameters.AddWithValue("$RetryCount", chunk.RetryCount);
                    if (chunk.LastErrorCode.HasValue)
                        insertChunk.Parameters.AddWithValue("$LastErrorCode", (int)chunk.LastErrorCode.Value);
                    else
                        insertChunk.Parameters.AddWithValue("$LastErrorCode", DBNull.Value);
                    insertChunk.Parameters.AddWithValue("$LastErrorMessage", (object?)chunk.LastErrorMessage ?? DBNull.Value);

                    await insertChunk.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            transaction.Commit();

            var chunkCount = task.Chunks?.Count ?? 0;
            _logger.Info("Download state saved.", eventCode: "STATE_STORE_SAVE", downloadId: task.Id, context: new { task.Status, task.Mode, ChunkCount = chunkCount });
        }
        catch (SqliteException ex) when (IsCorruptionError(ex))
        {
            transaction.Rollback();
            _logger.Error("SQLite state store corruption during save.", eventCode: "STATE_STORE_SAVE_CORRUPTED", exception: ex, downloadId: task.Id, context: new { DbPath = _dbPath });

            TryArchiveCorruptedDb();
            // After corruption, we don't attempt automatic re-save here; caller can decide to retry.
            throw;
        }
    }

    public async Task<List<DownloadTask>> LoadAllDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DownloadTask>();

        try
        {
            using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var downloads = new Dictionary<Guid, DownloadTask>();

            // Load downloads
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM Downloads;";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id")));

                    var task = new DownloadTask
                    {
                        Id = id,
                        Url = reader.GetString(reader.GetOrdinal("Url")),
                        FileName = reader.GetString(reader.GetOrdinal("FileName")),
                        SavePath = reader.GetString(reader.GetOrdinal("SavePath")),
                        TempFolderPath = reader.GetString(reader.GetOrdinal("TempFolderPath")),
                        Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                        Mode = (DownloadMode)reader.GetInt32(reader.GetOrdinal("Mode")),
                        ContentLength = reader.IsDBNull(reader.GetOrdinal("ContentLength"))
                            ? null
                            : reader.GetInt64(reader.GetOrdinal("ContentLength")),
                        SupportsRange = reader.GetInt32(reader.GetOrdinal("SupportsRange")) != 0,
                        ETag = reader.IsDBNull(reader.GetOrdinal("ETag"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("ETag")),
                        LastModified = reader.IsDBNull(reader.GetOrdinal("LastModified"))
                            ? null
                            : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastModified"))),
                        TotalDownloadedBytes = reader.GetInt64(reader.GetOrdinal("TotalDownloadedBytes")),
                        BytesWritten = reader.GetInt64(reader.GetOrdinal("BytesWritten")),
                        ConnectionsCount = reader.GetInt32(reader.GetOrdinal("ConnectionsCount")),
                        LastErrorCode = (DownloadErrorCode)reader.GetInt32(reader.GetOrdinal("LastErrorCode")),
                        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("LastErrorMessage"))
                    };

                    if (!reader.IsDBNull(reader.GetOrdinal("RequestHeaders")))
                    {
                        var rawHeaders = reader.GetString(reader.GetOrdinal("RequestHeaders"));
                        var headers = DeserializeHeaders(rawHeaders);
                        task.RequestHeaders = headers;
                    }

                    // Reset unsafe states to Paused
                    if (task.Status == DownloadStatus.Downloading
                        || task.Status == DownloadStatus.Merging
                        || task.Status == DownloadStatus.Verifying)
                    {
                        task.Status = DownloadStatus.Paused;
                    }

                    downloads[id] = task;
                }
            }

            // Load chunks
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM Chunks ORDER BY DownloadId, ChunkIndex;";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var downloadId = Guid.Parse(reader.GetString(reader.GetOrdinal("DownloadId")));

                    if (!downloads.TryGetValue(downloadId, out var task))
                    {
                        continue;
                    }

                    var chunk = new Chunk
                    {
                        Index = reader.GetInt32(reader.GetOrdinal("ChunkIndex")),
                        StartByte = reader.GetInt64(reader.GetOrdinal("StartByte")),
                        EndByte = reader.GetInt64(reader.GetOrdinal("EndByte")),
                        DownloadedBytes = reader.GetInt64(reader.GetOrdinal("DownloadedBytes")),
                        Status = (ChunkStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                        RetryCount = reader.GetInt32(reader.GetOrdinal("RetryCount")),
                        LastErrorCode = reader.IsDBNull(reader.GetOrdinal("LastErrorCode"))
                            ? null
                            : (DownloadErrorCode?)reader.GetInt32(reader.GetOrdinal("LastErrorCode")),
                        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("LastErrorMessage"))
                    };

                    task.Chunks.Add(chunk);
                }
            }

            foreach (var kvp in downloads)
            {
                var task = kvp.Value;
                task.UpdateProgressFromChunks();
                result.Add(task);
            }

            _logger.Info("Loaded downloads from state store.", eventCode: "STATE_STORE_LOAD", context: new { Count = result.Count });

            return result;
        }
        catch (SqliteException ex) when (IsCorruptionError(ex))
        {
            _logger.Error("SQLite state store corruption during load.", eventCode: "STATE_STORE_LOAD_CORRUPTED", exception: ex, context: new { DbPath = _dbPath });

            TryArchiveCorruptedDb();

            // After corruption, return empty list but keep app running.
            return new List<DownloadTask>();
        }
    }

    public async Task DeleteDownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        using var transaction = connection.BeginTransaction();

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM Chunks WHERE DownloadId = $DownloadId;";
                cmd.Parameters.AddWithValue("$DownloadId", id.ToString());
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM Downloads WHERE Id = $Id;";
                cmd.Parameters.AddWithValue("$Id", id.ToString());
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();

            _logger.Info("Deleted download from state store.", eventCode: "STATE_STORE_DELETE", downloadId: id);
        }
        catch (SqliteException ex) when (IsCorruptionError(ex))
        {
            transaction.Rollback();
            _logger.Error("SQLite state store corruption during delete.", eventCode: "STATE_STORE_DELETE_CORRUPTED", exception: ex, downloadId: id, context: new { DbPath = _dbPath });

            TryArchiveCorruptedDb();
            throw;
        }
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({table});";

        using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existingName = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(existingName, column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? SerializeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(headers);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? DeserializeHeaders(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (headers is null || headers.Count == 0)
            {
                return null;
            }

            return new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    private static bool IsCorruptionError(SqliteException ex)
    {
        // 11 = SQLITE_CORRUPT
        if (ex.SqliteErrorCode == 11)
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        return message.Contains("malformed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("file is encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not a database", StringComparison.OrdinalIgnoreCase);
    }

    private void TryArchiveCorruptedDb()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                var directory = Path.GetDirectoryName(_dbPath) ?? "";
                var fileName = Path.GetFileNameWithoutExtension(_dbPath);
                var ext = Path.GetExtension(_dbPath);
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var archiveName = $"{fileName}_corrupted_{timestamp}{ext}";
                var archivePath = Path.Combine(directory, archiveName);
                File.Move(_dbPath, archivePath, overwrite: false);
            }
        }
        catch
        {
            // Swallow; we don't want logging failures or IO issues to crash the app.
        }
    }
}
