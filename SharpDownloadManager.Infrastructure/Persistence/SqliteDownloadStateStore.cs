using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
    FinalFileName TEXT NOT NULL,
    SavePath TEXT NOT NULL,
    TempFolderPath TEXT NOT NULL,
    Status INTEGER NOT NULL,
    Mode INTEGER NOT NULL,
    ContentLength INTEGER NULL,
    SupportsRange INTEGER NOT NULL,
    ResumeCapability INTEGER NOT NULL DEFAULT 0,
    ETag TEXT NULL,
    LastModified TEXT NULL,
    TotalDownloadedBytes INTEGER NOT NULL,
    BytesWritten INTEGER NOT NULL DEFAULT 0,
    ActualFileSize INTEGER NULL,
    ConnectionsCount INTEGER NOT NULL,
    LastErrorCode INTEGER NOT NULL,
    LastErrorMessage TEXT NULL,
    RequestHeaders TEXT NULL,
    RequestMethod TEXT NOT NULL DEFAULT 'GET',
    RequestBody BLOB NULL,
    RequestBodyContentType TEXT NULL,
    TotalActiveSeconds REAL NOT NULL DEFAULT 0,
    LastResumedAt TEXT NULL,
    HttpStatusCategory INTEGER NOT NULL DEFAULT 0,
    TooManyRequestsRetryCount INTEGER NOT NULL DEFAULT 0,
    RetryBackoffSeconds REAL NOT NULL DEFAULT 0,
    NextRetryUtc TEXT NULL,
    MaxParallelConnections INTEGER NULL,
    SessionMetadata TEXT NULL
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
                    "ActualFileSize",
                    "INTEGER NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestHeaders",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestMethod",
                    "TEXT NOT NULL DEFAULT 'GET'",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "ResumeCapability",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "FinalFileName",
                    "TEXT NOT NULL DEFAULT ''",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "TotalActiveSeconds",
                    "REAL NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "LastResumedAt",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "HttpStatusCategory",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "TooManyRequestsRetryCount",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RetryBackoffSeconds",
                    "REAL NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "NextRetryUtc",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "MaxParallelConnections",
                    "INTEGER NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "SessionMetadata",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "FinalFileName",
                    "TEXT NOT NULL DEFAULT ''",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "TotalActiveSeconds",
                    "REAL NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "LastResumedAt",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "HttpStatusCategory",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "TooManyRequestsRetryCount",
                    "INTEGER NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RetryBackoffSeconds",
                    "REAL NOT NULL DEFAULT 0",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "NextRetryUtc",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "MaxParallelConnections",
                    "INTEGER NULL",
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
    FinalFileName TEXT NOT NULL,
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
    ActualFileSize INTEGER NULL,
    ConnectionsCount INTEGER NOT NULL,
    LastErrorCode INTEGER NOT NULL,
    LastErrorMessage TEXT NULL,
    RequestHeaders TEXT NULL,
    RequestMethod TEXT NOT NULL DEFAULT 'GET',
    RequestBody BLOB NULL,
    RequestBodyContentType TEXT NULL,
    TotalActiveSeconds REAL NOT NULL DEFAULT 0,
    LastResumedAt TEXT NULL,
    HttpStatusCategory INTEGER NOT NULL DEFAULT 0,
    TooManyRequestsRetryCount INTEGER NOT NULL DEFAULT 0,
    RetryBackoffSeconds REAL NOT NULL DEFAULT 0,
    NextRetryUtc TEXT NULL,
    MaxParallelConnections INTEGER NULL,
    SessionMetadata TEXT NULL
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
                    "ActualFileSize",
                    "INTEGER NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestHeaders",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestMethod",
                    "TEXT NOT NULL DEFAULT 'GET'",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestBody",
                    "BLOB NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "RequestBodyContentType",
                    "TEXT NULL",
                    cancellationToken)
                .ConfigureAwait(false);

            await EnsureColumnExistsAsync(
                    connection,
                    "Downloads",
                    "SessionMetadata",
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
    Id, Url, FileName, FinalFileName, SavePath, TempFolderPath,
    Status, Mode, ContentLength, SupportsRange, ResumeCapability,
    ETag, LastModified,
    TotalDownloadedBytes, BytesWritten, ActualFileSize, ConnectionsCount,
    LastErrorCode, LastErrorMessage,
    RequestHeaders, RequestMethod,
    RequestBody, RequestBodyContentType,
    TotalActiveSeconds, LastResumedAt, HttpStatusCategory,
    TooManyRequestsRetryCount, RetryBackoffSeconds, NextRetryUtc, MaxParallelConnections, SessionMetadata
) VALUES (
    $Id, $Url, $FileName, $FinalFileName, $SavePath, $TempFolderPath,
    $Status, $Mode, $ContentLength, $SupportsRange, $ResumeCapability,
    $ETag, $LastModified,
    $TotalDownloadedBytes, $BytesWritten, $ActualFileSize, $ConnectionsCount,
    $LastErrorCode, $LastErrorMessage,
    $RequestHeaders, $RequestMethod,
    $RequestBody, $RequestBodyContentType,
    $TotalActiveSeconds, $LastResumedAt, $HttpStatusCategory,
    $TooManyRequestsRetryCount, $RetryBackoffSeconds, $NextRetryUtc, $MaxParallelConnections, $SessionMetadata
)
ON CONFLICT(Id) DO UPDATE SET
    Url = excluded.Url,
    FileName = excluded.FileName,
    FinalFileName = excluded.FinalFileName,
    SavePath = excluded.SavePath,
    TempFolderPath = excluded.TempFolderPath,
    Status = excluded.Status,
    Mode = excluded.Mode,
    ContentLength = excluded.ContentLength,
    SupportsRange = excluded.SupportsRange,
    ResumeCapability = excluded.ResumeCapability,
    ETag = excluded.ETag,
    LastModified = excluded.LastModified,
    TotalDownloadedBytes = excluded.TotalDownloadedBytes,
    BytesWritten = excluded.BytesWritten,
    ActualFileSize = excluded.ActualFileSize,
    ConnectionsCount = excluded.ConnectionsCount,
    LastErrorCode = excluded.LastErrorCode,
    LastErrorMessage = excluded.LastErrorMessage,
    RequestHeaders = excluded.RequestHeaders,
    RequestMethod = excluded.RequestMethod,
    RequestBody = excluded.RequestBody,
    RequestBodyContentType = excluded.RequestBodyContentType,
    TotalActiveSeconds = excluded.TotalActiveSeconds,
    LastResumedAt = excluded.LastResumedAt,
    HttpStatusCategory = excluded.HttpStatusCategory,
    TooManyRequestsRetryCount = excluded.TooManyRequestsRetryCount,
    RetryBackoffSeconds = excluded.RetryBackoffSeconds,
    NextRetryUtc = excluded.NextRetryUtc,
    MaxParallelConnections = excluded.MaxParallelConnections,
    SessionMetadata = excluded.SessionMetadata;
";

                cmd.Parameters.AddWithValue("$Id", task.Id.ToString());
                cmd.Parameters.AddWithValue("$Url", task.Url);
                cmd.Parameters.AddWithValue("$FileName", task.FileName);
                var finalFileName = string.IsNullOrWhiteSpace(task.FinalFileName)
                    ? task.FileName
                    : task.FinalFileName;
                cmd.Parameters.AddWithValue("$FinalFileName", finalFileName);
                cmd.Parameters.AddWithValue("$SavePath", task.SavePath);
                cmd.Parameters.AddWithValue("$TempFolderPath", task.TempChunkFolderPath);
                cmd.Parameters.AddWithValue("$Status", (int)task.Status);
                cmd.Parameters.AddWithValue("$Mode", (int)task.Mode);
                if (task.ContentLength.HasValue)
                    cmd.Parameters.AddWithValue("$ContentLength", task.ContentLength.Value);
                else
                    cmd.Parameters.AddWithValue("$ContentLength", DBNull.Value);
                cmd.Parameters.AddWithValue("$SupportsRange", task.SupportsRange ? 1 : 0);
                cmd.Parameters.AddWithValue("$ResumeCapability", (int)task.ResumeCapability);
                cmd.Parameters.AddWithValue("$ETag", (object?)task.ETag ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$LastModified", task.LastModified?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$TotalDownloadedBytes", task.TotalDownloadedBytes);
                cmd.Parameters.AddWithValue("$BytesWritten", task.BytesWritten);
                if (task.ActualFileSize.HasValue)
                    cmd.Parameters.AddWithValue("$ActualFileSize", task.ActualFileSize.Value);
                else
                    cmd.Parameters.AddWithValue("$ActualFileSize", DBNull.Value);
                cmd.Parameters.AddWithValue("$ConnectionsCount", task.ConnectionsCount);
                cmd.Parameters.AddWithValue("$LastErrorCode", (int)task.LastErrorCode);
                cmd.Parameters.AddWithValue("$LastErrorMessage", (object?)task.LastErrorMessage ?? DBNull.Value);
                var headersJson = SerializeHeaders(task.RequestHeaders);
                cmd.Parameters.AddWithValue("$RequestHeaders", (object?)headersJson ?? DBNull.Value);
                var methodValue = string.IsNullOrWhiteSpace(task.RequestMethod)
                    ? "GET"
                    : task.RequestMethod.ToUpperInvariant();
                cmd.Parameters.AddWithValue("$RequestMethod", methodValue);
                if (task.RequestBody is { Length: > 0 })
                {
                    cmd.Parameters.AddWithValue("$RequestBody", task.RequestBody);
                }
                else
                {
                    cmd.Parameters.AddWithValue("$RequestBody", DBNull.Value);
                }
                cmd.Parameters.AddWithValue("$RequestBodyContentType", (object?)task.RequestBodyContentType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$TotalActiveSeconds", task.TotalActiveTime.TotalSeconds);
                cmd.Parameters.AddWithValue("$LastResumedAt", task.LastResumedAt?.ToString("o") ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$HttpStatusCategory", (int)task.HttpStatusCategory);
                cmd.Parameters.AddWithValue("$TooManyRequestsRetryCount", task.TooManyRequestsRetryCount);
                cmd.Parameters.AddWithValue("$RetryBackoffSeconds", task.RetryBackoff.TotalSeconds);
                cmd.Parameters.AddWithValue("$NextRetryUtc", task.NextRetryUtc?.ToString("o") ?? (object)DBNull.Value);
                if (task.MaxParallelConnections.HasValue)
                {
                    cmd.Parameters.AddWithValue("$MaxParallelConnections", task.MaxParallelConnections.Value);
                }
                else
                {
                    cmd.Parameters.AddWithValue("$MaxParallelConnections", DBNull.Value);
                }

                var sessionJson = SerializeSession(task.Session);
                cmd.Parameters.AddWithValue("$SessionMetadata", (object?)sessionJson ?? DBNull.Value);

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
                        FinalFileName = reader.IsDBNull(reader.GetOrdinal("FinalFileName"))
                            ? reader.GetString(reader.GetOrdinal("FileName"))
                            : (string.IsNullOrWhiteSpace(reader.GetString(reader.GetOrdinal("FinalFileName")))
                                ? reader.GetString(reader.GetOrdinal("FileName"))
                                : reader.GetString(reader.GetOrdinal("FinalFileName"))),
                        SavePath = reader.GetString(reader.GetOrdinal("SavePath")),
                        TempChunkFolderPath = reader.GetString(reader.GetOrdinal("TempFolderPath")),
                        Status = (DownloadStatus)reader.GetInt32(reader.GetOrdinal("Status")),
                        Mode = (DownloadMode)reader.GetInt32(reader.GetOrdinal("Mode")),
                        ContentLength = reader.IsDBNull(reader.GetOrdinal("ContentLength"))
                            ? null
                            : reader.GetInt64(reader.GetOrdinal("ContentLength")),
                        SupportsRange = reader.GetInt32(reader.GetOrdinal("SupportsRange")) != 0,
                        ResumeCapability = reader.IsDBNull(reader.GetOrdinal("ResumeCapability"))
                            ? DownloadResumeCapability.Unknown
                            : (DownloadResumeCapability)reader.GetInt32(reader.GetOrdinal("ResumeCapability")),
                        ETag = reader.IsDBNull(reader.GetOrdinal("ETag"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("ETag")),
                        TotalDownloadedBytes = reader.GetInt64(reader.GetOrdinal("TotalDownloadedBytes")),
                        BytesWritten = reader.GetInt64(reader.GetOrdinal("BytesWritten")),
                        ActualFileSize = reader.IsDBNull(reader.GetOrdinal("ActualFileSize"))
                            ? null
                            : reader.GetInt64(reader.GetOrdinal("ActualFileSize")),
                        ConnectionsCount = reader.GetInt32(reader.GetOrdinal("ConnectionsCount")),
                        LastErrorCode = (DownloadErrorCode)reader.GetInt32(reader.GetOrdinal("LastErrorCode")),
                        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("LastErrorMessage")),
                        HttpStatusCategory = reader.IsDBNull(reader.GetOrdinal("HttpStatusCategory"))
                            ? HttpStatusCategory.None
                            : (HttpStatusCategory)reader.GetInt32(reader.GetOrdinal("HttpStatusCategory")),
                        TooManyRequestsRetryCount = reader.IsDBNull(reader.GetOrdinal("TooManyRequestsRetryCount"))
                            ? 0
                            : reader.GetInt32(reader.GetOrdinal("TooManyRequestsRetryCount"))
                    };

                    if (!reader.IsDBNull(reader.GetOrdinal("LastModified")))
                    {
                        if (DateTimeOffset.TryParse(reader.GetString(reader.GetOrdinal("LastModified")), out var lastModified))
                        {
                            task.LastModified = lastModified;
                        }
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("TotalActiveSeconds")))
                    {
                        var seconds = reader.GetDouble(reader.GetOrdinal("TotalActiveSeconds"));
                        task.TotalActiveTime = TimeSpan.FromSeconds(seconds);
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("LastResumedAt")))
                    {
                        if (DateTimeOffset.TryParse(reader.GetString(reader.GetOrdinal("LastResumedAt")), out var lastResumed))
                        {
                            task.LastResumedAt = lastResumed;
                        }
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("RetryBackoffSeconds")))
                    {
                        var backoffSeconds = reader.GetDouble(reader.GetOrdinal("RetryBackoffSeconds"));
                        task.RetryBackoff = TimeSpan.FromSeconds(backoffSeconds);
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("NextRetryUtc")))
                    {
                        if (DateTimeOffset.TryParse(reader.GetString(reader.GetOrdinal("NextRetryUtc")), out var nextRetry))
                        {
                            task.NextRetryUtc = nextRetry;
                        }
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("MaxParallelConnections")))
                    {
                        task.MaxParallelConnections = reader.GetInt32(reader.GetOrdinal("MaxParallelConnections"));
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("RequestHeaders")))
                    {
                        var rawHeaders = reader.GetString(reader.GetOrdinal("RequestHeaders"));
                        var headers = DeserializeHeaders(rawHeaders);
                        task.RequestHeaders = headers;
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("RequestMethod")))
                    {
                        var method = reader.GetString(reader.GetOrdinal("RequestMethod"));
                        task.RequestMethod = string.IsNullOrWhiteSpace(method)
                            ? "GET"
                            : method.ToUpperInvariant();
                    }
                    else
                    {
                        task.RequestMethod = "GET";
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("RequestBody")))
                    {
                        task.RequestBody = (byte[])reader.GetValue(reader.GetOrdinal("RequestBody"));
                    }

                    if (!reader.IsDBNull(reader.GetOrdinal("RequestBodyContentType")))
                    {
                        var bodyType = reader.GetString(reader.GetOrdinal("RequestBodyContentType"));
                        task.RequestBodyContentType = string.IsNullOrWhiteSpace(bodyType)
                            ? null
                            : bodyType;
                    }

                    string? sessionJson = null;
                    if (!reader.IsDBNull(reader.GetOrdinal("SessionMetadata")))
                    {
                        sessionJson = reader.GetString(reader.GetOrdinal("SessionMetadata"));
                    }

                    task.Session = DeserializeSession(sessionJson, id, task);

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

    private static string? SerializeSession(DownloadSession? session)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(session);
        }
        catch
        {
            return null;
        }
    }

    private static DownloadSession DeserializeSession(string? json, Guid downloadId, DownloadTask task)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var session = JsonSerializer.Deserialize<DownloadSession>(json!);
                if (session is not null)
                {
                    session.DownloadId = downloadId;
                    session.PlannedFileName ??= task.FileName;
                    session.FinalFileName ??= task.FinalFileName;
                    session.FinalFilePath ??= task.SavePath;
                    session.TargetDirectory ??= Path.GetDirectoryName(task.SavePath);
                    return session;
                }
            }
            catch
            {
            }
        }

        var fallbackSession = new DownloadSession
        {
            DownloadId = downloadId,
            RequestedUrl = Uri.TryCreate(task.Url, UriKind.Absolute, out var requested) ? requested : null,
            NormalizedUrl = requested,
            FinalUrl = requested,
            HttpMethodUsedForProbe = task.RequestMethod ?? HttpMethod.Get.Method,
            PlannedFileName = task.FileName,
            FinalFileName = task.FinalFileName,
            FinalFilePath = task.SavePath,
            TargetDirectory = Path.GetDirectoryName(task.SavePath),
            ReportedFileSizeBytes = task.ContentLength,
            FileSizeSource = task.ContentLength.HasValue ? DownloadFileSizeSource.ContentLength : DownloadFileSizeSource.Unknown,
            SupportsResume = task.ResumeCapability == DownloadResumeCapability.Supported,
            AcceptRangesHeader = task.SupportsRange ? "bytes" : null,
            TemporaryFileName = task.FileName + ".part",
            BytesDownloadedSoFar = task.TotalDownloadedBytes,
            ContentLengthHeaderRaw = task.ContentLength?.ToString(),
            ContentType = null
        };

        return fallbackSession;
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
