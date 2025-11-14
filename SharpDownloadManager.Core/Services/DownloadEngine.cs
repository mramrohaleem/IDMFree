using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;
using SharpDownloadManager.Core.Utilities;

namespace SharpDownloadManager.Core.Services;

public class DownloadEngine : IDownloadEngine
{
    private readonly INetworkClient _networkClient;
    private readonly IDownloadStateStore _stateStore;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, DownloadTask> _downloads = new();
    private readonly object _syncRoot = new();
    private const int MaxConcurrentDownloads = 3;
    private readonly Dictionary<Guid, CancellationTokenSource> _activeTokens = new();
    private readonly Dictionary<Guid, Task> _activeDownloadTasks = new();
    private readonly Dictionary<Guid, DownloadCancellationReason> _cancellationReasons = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _scheduledRetryTokens = new();
    private readonly string _tempRootFolder;

    private enum DownloadCancellationReason
    {
        Unknown,
        UserPause,
        UserDelete,
        EngineShutdown,
        TooManyRequestsBackoff
    }

    private sealed class TooManyRequestsException : Exception
    {
        public TooManyRequestsException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    public DownloadEngine(
        INetworkClient networkClient,
        IDownloadStateStore stateStore,
        ILogger logger)
    {
        _networkClient = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tempRootFolder = InitializeTempRootFolder();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _stateStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var persistedDownloads = await _stateStore.LoadAllDownloadsAsync(cancellationToken).ConfigureAwait(false);

        lock (_syncRoot)
        {
            _downloads.Clear();
            _activeTokens.Clear();
            _activeDownloadTasks.Clear();
            foreach (var task in persistedDownloads)
            {
                _downloads[task.Id] = task;
            }
        }

        _logger.Info(
            $"DownloadEngine initialized with {persistedDownloads.Count} download(s).",
            eventCode: "DOWNLOAD_ENGINE_INIT");
    }

    public Task<IReadOnlyList<DownloadTask>> GetAllDownloadsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        List<DownloadTask> snapshot;
        lock (_syncRoot)
        {
            snapshot = new List<DownloadTask>(_downloads.Values);
        }

        return Task.FromResult<IReadOnlyList<DownloadTask>>(snapshot);
    }

    public async Task<DownloadTask> EnqueueDownloadAsync(
        string url,
        string? suggestedFileName,
        string saveFolderPath,
        DownloadMode mode = DownloadMode.Normal,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must be provided.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(saveFolderPath))
        {
            throw new ArgumentException("Save folder path must be provided.", nameof(saveFolderPath));
        }

        IReadOnlyDictionary<string, string>? normalizedHeaders = null;
        if (requestHeaders is not null && requestHeaders.Count > 0)
        {
            normalizedHeaders = new Dictionary<string, string>(requestHeaders, StringComparer.OrdinalIgnoreCase);
        }

        var resourceInfo = await _networkClient
            .ProbeAsync(url, normalizedHeaders, cancellationToken)
            .ConfigureAwait(false);

        var id = Guid.NewGuid();
        var fileName = ResolveFileName(
            suggestedFileName,
            resourceInfo.ContentDispositionFileName,
            resourceInfo.ContentType,
            resourceInfo.Url);
        var savePath = Path.Combine(saveFolderPath, fileName);
        var tempFolderPath = Path.Combine(_tempRootFolder, id.ToString("N"));

        var task = new DownloadTask
        {
            Id = id,
            Url = url,
            FileName = fileName,
            FinalFileName = fileName,
            SavePath = savePath,
            TempChunkFolderPath = tempFolderPath,
            Status = DownloadStatus.Queued,
            Mode = mode,
            ContentLength = resourceInfo.ContentLength,
            SupportsRange = resourceInfo.SupportsRange,
            ETag = resourceInfo.ETag,
            LastModified = resourceInfo.LastModified,
            RequestHeaders = normalizedHeaders,
            ActualFileSize = null
        };

        var connectionsCount = DetermineConnectionsCount(mode, resourceInfo);
        task.InitializeChunks(connectionsCount);
        task.MaxParallelConnections = connectionsCount;

        await _stateStore.SaveDownloadAsync(task, cancellationToken).ConfigureAwait(false);

        lock (_syncRoot)
        {
            _downloads[task.Id] = task;
            TryScheduleDownloads_NoLock();
        }

        _logger.Info(
            "Download enqueued.",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_ENQUEUED",
            context: new { task.Url, task.FileName, task.Mode, task.ContentLength });

        return task;
    }

    public async Task ResumeAsync(Guid downloadId, CancellationToken cancellationToken = default)
    {
        DownloadTask? task;
        bool shouldPersist = false;

        lock (_syncRoot)
        {
            if (!_downloads.TryGetValue(downloadId, out task))
            {
                return;
            }

            if (task.Status == DownloadStatus.Completed)
            {
                return;
            }

            if (task.Status != DownloadStatus.Queued)
            {
                task.Status = DownloadStatus.Queued;
                task.NextRetryUtc = null;
                task.RetryBackoff = TimeSpan.Zero;
                CancelScheduledRetry_NoLock(downloadId);
                if (task.HttpStatusCategory == HttpStatusCategory.TooManyRequests)
                {
                    task.HttpStatusCategory = HttpStatusCategory.None;
                    task.LastErrorCode = DownloadErrorCode.None;
                    task.LastErrorMessage = null;
                }
                shouldPersist = true;
            }
            else if (task.Status == DownloadStatus.Queued)
            {
                shouldPersist = true;
            }

            TryScheduleDownloads_NoLock();
        }

        if (shouldPersist && task is not null)
        {
            await _stateStore.SaveDownloadAsync(task, cancellationToken).ConfigureAwait(false);
            _logger.Info("Download resumed (queued).", downloadId: downloadId, eventCode: "DOWNLOAD_RESUMED");
        }
    }

    public async Task PauseAsync(Guid downloadId, CancellationToken cancellationToken = default)
    {
        DownloadTask? task;
        bool shouldPersist = false;
        CancellationTokenSource? ctsToCancel = null;

        lock (_syncRoot)
        {
            if (!_downloads.TryGetValue(downloadId, out task))
            {
                return;
            }

            if (task.Status == DownloadStatus.Completed || task.Status == DownloadStatus.Error)
            {
                return;
            }

            if (task.Status != DownloadStatus.Paused)
            {
                task.Status = DownloadStatus.Paused;
                task.MarkDownloadSuspended();
                CancelScheduledRetry_NoLock(downloadId);
                task.NextRetryUtc = null;
                task.RetryBackoff = TimeSpan.Zero;
                if (task.HttpStatusCategory == HttpStatusCategory.TooManyRequests)
                {
                    task.HttpStatusCategory = HttpStatusCategory.None;
                    task.LastErrorCode = DownloadErrorCode.None;
                    task.LastErrorMessage = null;
                }
                shouldPersist = true;
            }

            if (_activeTokens.TryGetValue(downloadId, out var cts))
            {
                _cancellationReasons[downloadId] = DownloadCancellationReason.UserPause;
                ctsToCancel = cts;
            }
            else
            {
                _cancellationReasons.Remove(downloadId);
            }
        }

        ctsToCancel?.Cancel();

        if (shouldPersist && task is not null)
        {
            await _stateStore.SaveDownloadAsync(task, cancellationToken).ConfigureAwait(false);
            _logger.Info("Download paused.", downloadId: downloadId, eventCode: "DOWNLOAD_PAUSED");
        }
    }

    public async Task DeleteAsync(Guid downloadId, CancellationToken cancellationToken = default)
    {
        bool removed;
        string? savePath = null;
        string? tempFolderPath = null;
        CancellationTokenSource? ctsToCancel = null;
        lock (_syncRoot)
        {
            if (_downloads.TryGetValue(downloadId, out var existing))
            {
                savePath = existing.SavePath;
                tempFolderPath = existing.TempChunkFolderPath;
            }

            removed = _downloads.Remove(downloadId);
            if (_activeTokens.TryGetValue(downloadId, out var cts))
            {
                _cancellationReasons[downloadId] = DownloadCancellationReason.UserDelete;
                ctsToCancel = cts;
            }
            else
            {
                _cancellationReasons.Remove(downloadId);
            }

            CancelScheduledRetry_NoLock(downloadId);
        }

        ctsToCancel?.Cancel();

        await _stateStore.DeleteDownloadAsync(downloadId, cancellationToken).ConfigureAwait(false);

        TryDeleteFile(savePath);
        TryDeleteDirectory(tempFolderPath);

        if (removed)
        {
            _logger.Info("Download deleted.", downloadId: downloadId, eventCode: "DOWNLOAD_DELETED");
        }
        else
        {
            _logger.Info("Download delete requested for non-tracked task.", downloadId: downloadId, eventCode: "DOWNLOAD_DELETE_REQUEST");
        }
    }

    private async Task RunDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        _logger.Info("Starting download.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_START");

        var taskUri = new Uri(task.Url);

        try
        {
            task.LastErrorCode = DownloadErrorCode.None;
            task.LastErrorMessage = null;
            task.HttpStatusCategory = HttpStatusCategory.None;
            task.Status = DownloadStatus.Downloading;
            CancelScheduledRetry(task.Id);
            task.MarkDownloadResumed();
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(task.TempChunkFolderPath);
            TryEnsureChunkFolderHidden(task.TempChunkFolderPath);

            if (task.Chunks.Count == 0)
            {
                var info = new HttpResourceInfo
                {
                    Url = taskUri,
                    ContentLength = task.ContentLength,
                    SupportsRange = task.SupportsRange,
                    ETag = task.ETag,
                    LastModified = task.LastModified,
                    IsChunkedWithoutLength = !task.ContentLength.HasValue
                };

                var connections = DetermineConnectionsCount(task.Mode, info);
                task.InitializeChunks(connections);
                task.MaxParallelConnections = connections;
                await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
            }

            SyncChunkProgressFromDisk(task);
            task.UpdateProgressFromChunks();
            await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);

            using var chunkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var chunkToken = chunkCts.Token;
            var parallelLimit = Math.Max(1, GetParallelChunkLimit(task));
            using var semaphore = new SemaphoreSlim(parallelLimit);

            var chunkTasks = new List<Task>();
            foreach (var chunk in task.Chunks)
            {
                if (chunk.Status == ChunkStatus.Completed)
                {
                    continue;
                }

                var chunkTask = Task.Run(async () =>
                {
                    await semaphore.WaitAsync(chunkToken).ConfigureAwait(false);
                    try
                    {
                        await DownloadChunkAsync(chunk, chunkToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, chunkToken);

                chunkTasks.Add(chunkTask);
            }

            if (chunkTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(chunkTasks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (await TryHandleTooManyRequestsAsync(ex, task, chunkCts, chunkTasks, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    throw;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                task.MarkDownloadSuspended();
                if (task.Status != DownloadStatus.Completed && task.Status != DownloadStatus.Error)
                {
                    task.Status = DownloadStatus.Paused;
                }

                await SaveStateAsync(task, cancellationToken, ignoreCancellation: true).ConfigureAwait(false);
                return;
            }

            task.MarkDownloadSuspended();
            task.Status = DownloadStatus.Merging;
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);

            var destinationDirectory = Path.GetDirectoryName(task.SavePath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            using (var destinationStream = new FileStream(task.SavePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var chunk in task.Chunks.OrderBy(c => c.Index))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunkPath = Path.Combine(task.TempChunkFolderPath, $"{chunk.Index}.part");
                    if (!File.Exists(chunkPath))
                    {
                        throw new IOException($"Missing chunk file {chunkPath} for merge.");
                    }

                    using var chunkStream = new FileStream(chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await chunkStream.CopyToAsync(destinationStream, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var chunk in task.Chunks)
            {
                var chunkPath = Path.Combine(task.TempChunkFolderPath, $"{chunk.Index}.part");
                if (File.Exists(chunkPath))
                {
                    try
                    {
                        File.Delete(chunkPath);
                    }
                    catch
                    {
                        // Ignore failures deleting temp files; they can be cleaned later.
                    }
                }
            }

            TryDeleteDirectory(task.TempChunkFolderPath);

            var finalFileInfo = new FileInfo(task.SavePath);
            var finalLength = finalFileInfo.Length;
            task.BytesWritten = finalLength;
            task.TotalDownloadedBytes = finalLength;
            task.ActualFileSize = finalLength;

            if (task.ContentLength.HasValue)
            {
                if (finalLength != task.ContentLength.Value)
                {
                    var message =
                        $"Downloaded size does not match Content-Length. Expected {task.ContentLength.Value} bytes but received {finalLength} bytes.";

                    task.MarkAsError(DownloadErrorCode.ValidationMismatch, message);
                    task.MarkDownloadSuspended();
                    task.ActualFileSize = null;
                    TryDeleteFile(task.SavePath);
                    TryDeleteDirectory(task.TempChunkFolderPath);
                    await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);

                    _logger.Error(
                        "Download validation failed due to size mismatch.",
                        downloadId: task.Id,
                        eventCode: "DOWNLOAD_VALIDATION_MISMATCH",
                        context: new
                        {
                            task.Url,
                            Expected = task.ContentLength.Value,
                            Actual = finalLength
                        });

                    return;
                }
            }
            else if (finalLength == 0)
            {
                const string zeroMessage = "Download completed with zero bytes written.";
                task.MarkAsError(DownloadErrorCode.ZeroBytes, zeroMessage);
                task.MarkDownloadSuspended();
                task.ActualFileSize = null;
                TryDeleteFile(task.SavePath);
                TryDeleteDirectory(task.TempChunkFolderPath);
                await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);

                _logger.Error(
                    "Download validation failed due to zero-byte file.",
                    downloadId: task.Id,
                    eventCode: "DOWNLOAD_VALIDATION_ZERO_BYTES",
                    context: new { task.Url });

                return;
            }

            task.LastErrorCode = DownloadErrorCode.None;
            task.LastErrorMessage = null;
            task.Status = DownloadStatus.Completed;
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
            _logger.Info("Download completed.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_COMPLETED");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            task.MarkDownloadSuspended();
            if (task.Status != DownloadStatus.Completed && task.Status != DownloadStatus.Error)
            {
                task.Status = DownloadStatus.Paused;
            }

            await SaveStateAsync(task, cancellationToken, ignoreCancellation: true).ConfigureAwait(false);
            var reason = ConsumeCancellationReason(task.Id);
            _logger.Info(
                "Download canceled.",
                downloadId: task.Id,
                eventCode: "DOWNLOAD_RUN_CANCELED",
                context: new { task.Url, Reason = reason.ToString() });
        }
        catch (HttpRequestException httpEx)
        {
            task.MarkDownloadSuspended();
            DownloadErrorCode code;

            if (httpEx.Data.Contains("CustomErrorCode") &&
                httpEx.Data["CustomErrorCode"] is DownloadErrorCode customCode)
            {
                code = customCode;
            }
            else
            {
                code = MapHttpErrorCode(httpEx.StatusCode);
            }

            task.MarkAsError(code, httpEx.Message);
            task.ActualFileSize = null;
            TryDeleteFile(task.SavePath);
            await SaveStateAsync(task, CancellationToken.None, ignoreCancellation: true).ConfigureAwait(false);
            _logger.Error(
                "Download failed due to HTTP error.",
                downloadId: task.Id,
                eventCode: "DOWNLOAD_RUN_HTTP_ERROR",
                exception: httpEx,
                context: new
                {
                    Url = task.Url,
                    StatusCode = (int?)httpEx.StatusCode,
                    ErrorCode = code.ToString()
                });
        }
        catch (Exception ex)
        {
            task.MarkDownloadSuspended();
            var code = ex switch
            {
                UnauthorizedAccessException => DownloadErrorCode.DiskPermissionDenied,
                IOException => DownloadErrorCode.DiskNoSpace,
                _ => DownloadErrorCode.Unknown
            };

            task.MarkAsError(code, ex.Message);
            task.ActualFileSize = null;
            TryDeleteFile(task.SavePath);
            await SaveStateAsync(task, CancellationToken.None, ignoreCancellation: true).ConfigureAwait(false);
            _logger.Error(
                "Download failed.",
                downloadId: task.Id,
                eventCode: "DOWNLOAD_RUN_ERROR",
                exception: ex,
                context: new
                {
                    Url = task.Url,
                    ErrorCode = code.ToString()
                });
        }
        finally
        {
            lock (_syncRoot)
            {
                _cancellationReasons.Remove(task.Id);
                CancelScheduledRetry_NoLock(task.Id);
            }
            _logger.Info("Download finished.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_FINISHED");
        }

        async Task DownloadChunkAsync(Chunk chunk, CancellationToken ct)
        {
            var chunkPath = Path.Combine(task.TempChunkFolderPath, $"{chunk.Index}.part");
            var expectedLength = chunk.EndByte >= 0 ? chunk.EndByte - chunk.StartByte + 1 : (long?)null;

            long existingLength = 0;
            if (File.Exists(chunkPath))
            {
                existingLength = new FileInfo(chunkPath).Length;
                if (expectedLength.HasValue && existingLength > expectedLength.Value)
                {
                    using var truncate = new FileStream(chunkPath, FileMode.Open, FileAccess.Write, FileShare.Read);
                    truncate.SetLength(expectedLength.Value);
                    existingLength = expectedLength.Value;
                }
            }

            if (task.SupportsRange && existingLength > 0 && existingLength != chunk.DownloadedBytes)
            {
                chunk.DownloadedBytes = existingLength;
            }

            if (!task.SupportsRange && chunk.Index == 0 && chunk.DownloadedBytes > 0)
            {
                chunk.DownloadedBytes = 0;
                existingLength = 0;
                try
                {
                    if (File.Exists(chunkPath))
                    {
                        File.Delete(chunkPath);
                    }
                }
                catch
                {
                }
            }

            if (expectedLength.HasValue && chunk.DownloadedBytes >= expectedLength.Value)
            {
                chunk.Status = ChunkStatus.Completed;
                chunk.LastErrorCode = null;
                chunk.LastErrorMessage = null;
                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            chunk.Status = ChunkStatus.Downloading;
            chunk.LastErrorCode = null;
            chunk.LastErrorMessage = null;

            long? from = null;
            long? to = null;
            if (task.SupportsRange && chunk.EndByte >= 0)
            {
                var start = chunk.StartByte + chunk.DownloadedBytes;
                var end = chunk.EndByte;
                if (start > end)
                {
                    chunk.Status = ChunkStatus.Completed;
                    chunk.LastErrorCode = null;
                    chunk.LastErrorMessage = null;
                    task.UpdateProgressFromChunks();
                    await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                from = start;
                to = end;
            }

            var fileMode = chunk.DownloadedBytes > 0 ? FileMode.Append : FileMode.Create;

            try
            {
                using var fileStream = new FileStream(chunkPath, fileMode, FileAccess.Write, FileShare.Read);
                if (fileMode == FileMode.Append)
                {
                    fileStream.Seek(0, SeekOrigin.End);
                }

                var progress = new Progress<long>(delta =>
                {
                    if (delta <= 0)
                    {
                        return;
                    }

                    chunk.DownloadedBytes += delta;
                    if (expectedLength.HasValue && chunk.DownloadedBytes > expectedLength.Value)
                    {
                        chunk.DownloadedBytes = expectedLength.Value;
                    }

                    task.UpdateProgressFromChunks();
                });

                var allowFallback = !from.HasValue && !to.HasValue;

                var responseMetadata = await _networkClient.DownloadRangeToStreamAsync(
                        taskUri,
                        from,
                        to,
                        fileStream,
                        progress,
                        ct,
                        allowFallback,
                        task.RequestHeaders)
                    .ConfigureAwait(false);

                TryUpdateTaskMetadataFromResponse(task, responseMetadata);

                chunk.Status = ChunkStatus.Completed;
                chunk.LastErrorCode = null;
                chunk.LastErrorMessage = null;
                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    chunk.Status = ChunkStatus.Error;
                    chunk.LastErrorCode = DownloadErrorCode.Unknown;
                    chunk.LastErrorMessage = ex.Message;
                    task.UpdateProgressFromChunks();
                    await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                if (task.HttpStatusCategory == HttpStatusCategory.TooManyRequests)
                {
                    chunk.Status = ChunkStatus.Throttled;
                    chunk.LastErrorCode = DownloadErrorCode.Http429;
                }
                else
                {
                    chunk.Status = ChunkStatus.Paused;
                    chunk.LastErrorCode = null;
                }

                chunk.LastErrorMessage = null;
                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.TooManyRequests)
            {
                chunk.Status = ChunkStatus.Throttled;
                chunk.LastErrorCode = DownloadErrorCode.Http429;
                chunk.LastErrorMessage = httpEx.Message;
                task.HttpStatusCategory = HttpStatusCategory.TooManyRequests;
                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                throw new TooManyRequestsException(httpEx.Message, httpEx);
            }
            catch (HttpRequestException httpEx)
            {
                chunk.Status = ChunkStatus.Error;
                chunk.LastErrorMessage = httpEx.Message;
                if (httpEx.StatusCode.HasValue)
                {
                    var statusValue = (int)httpEx.StatusCode.Value;
                    if (statusValue >= 400 && statusValue < 500)
                    {
                        chunk.LastErrorCode = DownloadErrorCode.Http4xx;
                    }
                    else if (statusValue >= 500)
                    {
                        chunk.LastErrorCode = DownloadErrorCode.Http5xx;
                    }
                    else
                    {
                        chunk.LastErrorCode = DownloadErrorCode.ServerError;
                    }
                }
                else
                {
                    chunk.LastErrorCode = DownloadErrorCode.ServerError;
                }

                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                chunk.Status = ChunkStatus.Error;
                chunk.LastErrorCode = DownloadErrorCode.Unknown;
                chunk.LastErrorMessage = ex.Message;
                task.UpdateProgressFromChunks();
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static int GetParallelChunkLimit(DownloadTask task)
    {
        if (task is null)
        {
            return 1;
        }

        var limit = task.MaxParallelConnections ?? task.ConnectionsCount;
        if (limit <= 0)
        {
            limit = task.ConnectionsCount;
        }

        return limit > 0 ? limit : 1;
    }

    private void SyncChunkProgressFromDisk(DownloadTask task)
    {
        if (task is null)
        {
            return;
        }

        foreach (var chunk in task.Chunks)
        {
            try
            {
                var chunkPath = Path.Combine(task.TempChunkFolderPath, $"{chunk.Index}.part");
                if (!File.Exists(chunkPath))
                {
                    if (chunk.Status is ChunkStatus.Error or ChunkStatus.Paused or ChunkStatus.Throttled)
                    {
                        chunk.Status = ChunkStatus.Pending;
                        chunk.LastErrorCode = null;
                        chunk.LastErrorMessage = null;
                    }

                    continue;
                }

                var fileInfo = new FileInfo(chunkPath);
                var length = fileInfo.Length;
                var expectedLength = chunk.EndByte >= 0 ? chunk.EndByte - chunk.StartByte + 1 : (long?)null;
                if (expectedLength.HasValue && length > expectedLength.Value)
                {
                    length = expectedLength.Value;
                }

                chunk.DownloadedBytes = length;

                if (expectedLength.HasValue && chunk.DownloadedBytes >= expectedLength.Value)
                {
                    chunk.Status = ChunkStatus.Completed;
                    chunk.LastErrorCode = null;
                    chunk.LastErrorMessage = null;
                }
                else if (chunk.Status is ChunkStatus.Error or ChunkStatus.Paused or ChunkStatus.Throttled)
                {
                    chunk.Status = ChunkStatus.Pending;
                    chunk.LastErrorCode = null;
                    chunk.LastErrorMessage = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    "Failed to synchronize chunk progress from disk.",
                    downloadId: task.Id,
                    eventCode: "CHUNK_SYNC_FAILED",
                    context: new { chunk.Index, ex.Message });
            }
        }

        task.UpdateProgressFromChunks();
    }

    private async Task<bool> TryHandleTooManyRequestsAsync(
        Exception exception,
        DownloadTask task,
        CancellationTokenSource chunkCancellation,
        IReadOnlyList<Task> chunkTasks,
        CancellationToken cancellationToken)
    {
        if (exception is null)
        {
            return false;
        }

        var aggregate = exception as AggregateException ?? new AggregateException(exception);
        TooManyRequestsException? throttled = null;
        foreach (var inner in aggregate.Flatten().InnerExceptions)
        {
            if (inner is TooManyRequestsException tooManyRequestsException)
            {
                throttled = tooManyRequestsException;
                break;
            }
        }

        if (throttled is null)
        {
            return false;
        }

        try
        {
            chunkCancellation.Cancel();
        }
        catch
        {
        }

        if (chunkTasks is not null)
        {
            try
            {
                await Task.WhenAll(chunkTasks).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        task.MarkDownloadSuspended();
        task.Status = DownloadStatus.Throttled;
        task.HttpStatusCategory = HttpStatusCategory.TooManyRequests;
        task.LastErrorCode = DownloadErrorCode.Http429;
        var message = string.IsNullOrWhiteSpace(throttled.Message)
            ? "Server returned 429 Too Many Requests. Waiting before retrying."
            : throttled.Message;
        task.LastErrorMessage = message;

        task.TooManyRequestsRetryCount++;
        var attempt = Math.Max(1, task.TooManyRequestsRetryCount);
        var delaySeconds = (int)Math.Min(300, 30 * Math.Pow(2, Math.Max(0, attempt - 1)));
        task.RetryBackoff = TimeSpan.FromSeconds(delaySeconds);
        task.NextRetryUtc = DateTimeOffset.UtcNow + task.RetryBackoff;
        task.MaxParallelConnections = 1;

        foreach (var chunk in task.Chunks)
        {
            if (chunk.Status == ChunkStatus.Completed)
            {
                continue;
            }

            chunk.Status = ChunkStatus.Throttled;
            chunk.LastErrorCode = DownloadErrorCode.Http429;
            chunk.LastErrorMessage = message;
        }

        task.UpdateProgressFromChunks();

        await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);

        _logger.Warn(
            "Download temporarily throttled by remote server (HTTP 429).",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_TOO_MANY_REQUESTS",
            context: new
            {
                task.Url,
                RetryInSeconds = delaySeconds
            });

        ScheduleRetryAfterBackoff(task);

        return true;
    }

    private void ScheduleRetryAfterBackoff(DownloadTask task)
    {
        if (task is null)
        {
            return;
        }

        var delay = task.RetryBackoff;
        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromSeconds(30);
        }

        var downloadId = task.Id;
        CancellationTokenSource retryCts;

        lock (_syncRoot)
        {
            CancelScheduledRetry_NoLock(downloadId);
            retryCts = new CancellationTokenSource();
            _scheduledRetryTokens[downloadId] = retryCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, retryCts.Token).ConfigureAwait(false);

                DownloadTask? target = null;
                lock (_syncRoot)
                {
                    _scheduledRetryTokens.Remove(downloadId);
                    if (_downloads.TryGetValue(downloadId, out target))
                    {
                        if (target.Status == DownloadStatus.Throttled)
                        {
                            target.Status = DownloadStatus.Queued;
                            target.LastErrorMessage = null;
                            target.LastErrorCode = DownloadErrorCode.None;
                            target.HttpStatusCategory = HttpStatusCategory.None;
                            target.NextRetryUtc = null;
                            target.RetryBackoff = TimeSpan.Zero;
                            _cancellationReasons[downloadId] = DownloadCancellationReason.TooManyRequestsBackoff;
                            TryScheduleDownloads_NoLock();
                        }
                        else
                        {
                            target = null;
                        }
                    }
                }

                if (target is not null)
                {
                    try
                    {
                        await SaveStateAsync(target, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(
                            "Failed to persist retry state after HTTP 429 backoff.",
                            downloadId: downloadId,
                            eventCode: "DOWNLOAD_RETRY_STATE_SAVE_FAILED",
                            context: new { ex.Message });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                lock (_syncRoot)
                {
                    _scheduledRetryTokens.Remove(downloadId);
                }
            }
            finally
            {
                retryCts.Dispose();
            }
        });
    }

    private void CancelScheduledRetry(Guid downloadId)
    {
        lock (_syncRoot)
        {
            CancelScheduledRetry_NoLock(downloadId);
        }
    }

    private void CancelScheduledRetry_NoLock(Guid downloadId)
    {
        if (_scheduledRetryTokens.TryGetValue(downloadId, out var cts))
        {
            _scheduledRetryTokens.Remove(downloadId);
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }
    }

    private void TryEnsureChunkFolderHidden(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var directoryInfo = new DirectoryInfo(folderPath);
            if (!directoryInfo.Attributes.HasFlag(FileAttributes.Hidden))
            {
                directoryInfo.Attributes |= FileAttributes.Hidden;
            }
        }
        catch
        {
        }
    }

    private DownloadCancellationReason ConsumeCancellationReason(Guid downloadId)
    {
        lock (_syncRoot)
        {
            if (_cancellationReasons.TryGetValue(downloadId, out var reason))
            {
                _cancellationReasons.Remove(downloadId);
                return reason;
            }
        }

        return DownloadCancellationReason.Unknown;
    }

    private void TryScheduleDownloads_NoLock()
    {
        while (_activeDownloadTasks.Count < MaxConcurrentDownloads)
        {
            DownloadTask? nextTask = null;
            foreach (var candidate in _downloads.Values)
            {
                if (candidate.Status == DownloadStatus.Queued && !_activeDownloadTasks.ContainsKey(candidate.Id))
                {
                    nextTask = candidate;
                    break;
                }
            }

            if (nextTask is null)
            {
                break;
            }

            StartDownload_NoLock(nextTask);
        }
    }

    private void StartDownload_NoLock(DownloadTask task)
    {
        if (_activeDownloadTasks.ContainsKey(task.Id))
        {
            return;
        }

        CancelScheduledRetry_NoLock(task.Id);

        var cts = new CancellationTokenSource();
        _activeTokens[task.Id] = cts;

        var runningTask = Task.Run(async () =>
        {
            try
            {
                await RunDownloadAsync(task, cts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeDownloadTasks.Remove(task.Id);
                    _activeTokens.Remove(task.Id);
                    TryScheduleDownloads_NoLock();
                }
            }
        });

        _activeDownloadTasks[task.Id] = runningTask;
    }

    private Task SaveStateAsync(DownloadTask task, CancellationToken cancellationToken, bool ignoreCancellation = false)
    {
        var token = ignoreCancellation ? CancellationToken.None : cancellationToken;
        return _stateStore.SaveDownloadAsync(task, token);
    }

    private string InitializeTempRootFolder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var candidate = Path.Combine(localAppData, "SharpDownloadManager", "chunks");
            try
            {
                Directory.CreateDirectory(candidate);
                TryEnsureChunkFolderHidden(candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    "Failed to prepare LocalAppData chunk folder. Falling back to system temp.",
                    eventCode: "TEMP_ROOT_FALLBACK",
                    context: new { candidate, ex.Message });
            }
        }

        var fallback = Path.Combine(Path.GetTempPath(), "SharpDownloadManager", "chunks");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private void TryUpdateTaskMetadataFromResponse(DownloadTask task, DownloadResponseMetadata metadata)
    {
        if (task is null || metadata is null)
        {
            return;
        }

        if (metadata.ResourceLength.HasValue)
        {
            var length = metadata.ResourceLength.Value;
            if (!task.ContentLength.HasValue || task.ContentLength.Value != length)
            {
                task.ContentLength = length;

                if (task.Chunks.Count == 1)
                {
                    var singleChunk = task.Chunks[0];
                    if (singleChunk.EndByte < 0)
                    {
                        singleChunk.EndByte = length > 0 ? length - 1 : -1;
                    }
                }
            }
        }
        else if (metadata.ResponseContentLength.HasValue)
        {
            var responseLength = metadata.ResponseContentLength.Value;
            if (responseLength > 0 && (!task.ContentLength.HasValue || task.ContentLength.Value != responseLength))
            {
                task.ContentLength = responseLength;

                if (task.Chunks.Count == 1)
                {
                    var singleChunk = task.Chunks[0];
                    if (singleChunk.EndByte < 0)
                    {
                        singleChunk.EndByte = responseLength > 0 ? responseLength - 1 : -1;
                    }
                }
            }
        }

        if (metadata.SupportsRange && !task.SupportsRange)
        {
            task.SupportsRange = true;
        }

        if (!string.IsNullOrEmpty(metadata.ETag) && string.IsNullOrEmpty(task.ETag))
        {
            task.ETag = metadata.ETag;
        }

        if (metadata.LastModified.HasValue && task.LastModified is null)
        {
            task.LastModified = metadata.LastModified;
        }

        if (!string.IsNullOrWhiteSpace(metadata.ContentDispositionFileName) ||
            !string.IsNullOrWhiteSpace(metadata.ContentType))
        {
            try
            {
                var resolvedName = ResolveFileName(
                    task.FileName,
                    metadata.ContentDispositionFileName,
                    metadata.ContentType,
                    new Uri(task.Url));

                if (!string.Equals(task.FileName, resolvedName, StringComparison.Ordinal))
                {
                    var directory = Path.GetDirectoryName(task.SavePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        var newSavePath = Path.Combine(directory, resolvedName);

                        _logger.Info(
                            "Updating file name based on server response metadata.",
                            downloadId: task.Id,
                            eventCode: "DOWNLOAD_FILENAME_UPDATED",
                            context: new { Old = task.FileName, New = resolvedName });

                        task.FileName = resolvedName;
                        task.FinalFileName = resolvedName;
                        task.SavePath = newSavePath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    "Failed to adjust file name from response metadata.",
                    downloadId: task.Id,
                    eventCode: "DOWNLOAD_FILENAME_UPDATE_FAILED",
                    context: new { ex.Message });
            }
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static DownloadErrorCode MapHttpErrorCode(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return DownloadErrorCode.NetUnreachable;
        }

        var codeValue = (int)statusCode.Value;

        if (statusCode.Value == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return DownloadErrorCode.RangeNotSupported;
        }

        if (codeValue >= 400 && codeValue < 500)
        {
            return DownloadErrorCode.Http4xx;
        }

        if (codeValue >= 500)
        {
            return DownloadErrorCode.Http5xx;
        }

        return DownloadErrorCode.ServerError;
    }

    private static int DetermineConnectionsCount(DownloadMode mode, HttpResourceInfo info)
    {
        if (mode == DownloadMode.SafeMode)
        {
            return 1;
        }

        if (info.IsChunkedWithoutLength || !info.SupportsRange)
        {
            return 1;
        }

        if (info.ContentLength.HasValue && info.ContentLength.Value < 1_000_000)
        {
            return 2;
        }

        return 4;
    }

    private static readonly Dictionary<string, string> KnownContentTypeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/pdf"] = ".pdf",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
        ["application/octet-stream"] = ".bin",
        ["application/x-msdownload"] = ".exe",
        ["application/x-7z-compressed"] = ".7z",
        ["application/gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/json"] = ".json",
        ["text/plain"] = ".txt",
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["video/mp4"] = ".mp4",
        ["audio/mpeg"] = ".mp3"
    };

    private static string ResolveFileName(
        string? browserFileName,
        string? contentDispositionFileName,
        string? contentType,
        Uri resourceUri)
    {
        var normalizedDisposition = FileNameHelper.NormalizeFileName(contentDispositionFileName);
        var normalizedBrowser = FileNameHelper.NormalizeFileName(browserFileName);
        var normalizedFromUrl = FileNameHelper.TryExtractFileNameFromUrl(resourceUri);

        var candidate = normalizedDisposition ?? normalizedBrowser;

        if (FileNameHelper.LooksLikePlaceholderName(candidate) &&
            !FileNameHelper.LooksLikePlaceholderName(normalizedFromUrl))
        {
            candidate = normalizedFromUrl;
        }

        candidate ??= normalizedFromUrl;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = "download";
        }

        var extension = Path.GetExtension(candidate);
        if (string.IsNullOrWhiteSpace(extension) || extension.Equals(".", StringComparison.Ordinal))
        {
            var resolvedExtension = TryResolveExtensionFromContentType(contentType);
            if (!string.IsNullOrEmpty(resolvedExtension))
            {
                candidate = candidate.TrimEnd('.') + resolvedExtension;
            }
            else if (!candidate.Contains('.', StringComparison.Ordinal))
            {
                candidate += ".bin";
            }
        }

        return candidate;
    }

    private static string? TryResolveExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var normalized = contentType.Split(';')[0].Trim();
        if (KnownContentTypeExtensions.TryGetValue(normalized, out var extension))
        {
            return extension;
        }

        return null;
    }
}
