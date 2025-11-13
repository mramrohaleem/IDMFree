using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;

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

    public DownloadEngine(
        INetworkClient networkClient,
        IDownloadStateStore stateStore,
        ILogger logger)
    {
        _networkClient = networkClient ?? throw new ArgumentNullException(nameof(networkClient));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var resourceInfo = await _networkClient.ProbeAsync(url, cancellationToken).ConfigureAwait(false);

        var id = Guid.NewGuid();
        var fileName = ResolveFileName(suggestedFileName, resourceInfo.Url);
        var savePath = Path.Combine(saveFolderPath, fileName);
        var tempFolderPath = Path.Combine(saveFolderPath, ".sharpdm", id.ToString("N"));

        var task = new DownloadTask
        {
            Id = id,
            Url = url,
            FileName = fileName,
            SavePath = savePath,
            TempFolderPath = tempFolderPath,
            Status = DownloadStatus.Queued,
            Mode = mode,
            ContentLength = resourceInfo.ContentLength,
            SupportsRange = resourceInfo.SupportsRange,
            ETag = resourceInfo.ETag,
            LastModified = resourceInfo.LastModified
        };

        var connectionsCount = DetermineConnectionsCount(mode, resourceInfo);
        task.InitializeChunks(connectionsCount);

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
                shouldPersist = true;
            }

            if (_activeTokens.TryGetValue(downloadId, out var cts))
            {
                ctsToCancel = cts;
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
        CancellationTokenSource? ctsToCancel = null;
        lock (_syncRoot)
        {
            removed = _downloads.Remove(downloadId);
            if (_activeTokens.TryGetValue(downloadId, out var cts))
            {
                ctsToCancel = cts;
            }
        }

        ctsToCancel?.Cancel();

        await _stateStore.DeleteDownloadAsync(downloadId, cancellationToken).ConfigureAwait(false);

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

        try
        {
            task.LastErrorCode = DownloadErrorCode.None;
            task.LastErrorMessage = null;
            task.Status = DownloadStatus.Downloading;
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(task.TempFolderPath);
            var taskUri = new Uri(task.Url);

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
                await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
            }

            async Task DownloadChunkAsync(Chunk chunk, CancellationToken ct)
            {
                var chunkPath = Path.Combine(task.TempFolderPath, $"{chunk.Index}.part");
                bool resume = false;

                if (File.Exists(chunkPath))
                {
                    var fileInfo = new FileInfo(chunkPath);
                    if (task.SupportsRange && chunk.DownloadedBytes > 0 && fileInfo.Length == chunk.DownloadedBytes)
                    {
                        resume = true;
                    }
                    else
                    {
                        chunk.DownloadedBytes = 0;
                        try
                        {
                            File.Delete(chunkPath);
                        }
                        catch
                        {
                            // Ignore failures deleting temp chunk files; they will be overwritten.
                        }
                    }
                }
                else if (chunk.DownloadedBytes > 0)
                {
                    chunk.DownloadedBytes = 0;
                }

                if (chunk.EndByte >= 0)
                {
                    var chunkLength = chunk.EndByte - chunk.StartByte + 1;
                    if (chunk.DownloadedBytes >= chunkLength)
                    {
                        chunk.Status = ChunkStatus.Completed;
                        chunk.LastErrorCode = null;
                        chunk.LastErrorMessage = null;
                        task.UpdateProgressFromChunks();
                        await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }
                }

                chunk.Status = ChunkStatus.Downloading;

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

                var fileMode = resume ? FileMode.Append : FileMode.Create;

                try
                {
                    using var fileStream = new FileStream(chunkPath, fileMode, FileAccess.Write, FileShare.Read);
                    var progress = new Progress<long>(delta =>
                    {
                        if (delta <= 0)
                        {
                            return;
                        }

                        chunk.DownloadedBytes += delta;
                        task.UpdateProgressFromChunks();
                    });

                    await _networkClient.DownloadRangeToStreamAsync(
                        taskUri,
                        from,
                        to,
                        fileStream,
                        progress,
                        ct).ConfigureAwait(false);

                    chunk.Status = ChunkStatus.Completed;
                    chunk.LastErrorCode = null;
                    chunk.LastErrorMessage = null;
                    task.UpdateProgressFromChunks();
                    await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    chunk.Status = ChunkStatus.Paused;
                    chunk.LastErrorCode = null;
                    chunk.LastErrorMessage = null;
                    task.UpdateProgressFromChunks();
                    await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                catch (HttpRequestException httpEx)
                {
                    chunk.Status = ChunkStatus.Error;
                    chunk.LastErrorMessage = httpEx.Message;
                    if (httpEx.StatusCode.HasValue)
                    {
                        if ((int)httpEx.StatusCode.Value >= 400 && (int)httpEx.StatusCode.Value < 500)
                        {
                            chunk.LastErrorCode = DownloadErrorCode.Http4xx;
                        }
                        else if ((int)httpEx.StatusCode.Value >= 500)
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

            var chunkTasks = task.Chunks.Select(chunk => DownloadChunkAsync(chunk, cancellationToken)).ToList();
            await Task.WhenAll(chunkTasks).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                task.Status = DownloadStatus.Paused;
                await SaveStateAsync(task, cancellationToken, ignoreCancellation: true).ConfigureAwait(false);
                return;
            }

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

                    var chunkPath = Path.Combine(task.TempFolderPath, $"{chunk.Index}.part");
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
                var chunkPath = Path.Combine(task.TempFolderPath, $"{chunk.Index}.part");
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

            if (task.ContentLength.HasValue)
            {
                var finalLength = new FileInfo(task.SavePath).Length;
                if (finalLength != task.ContentLength.Value)
                {
                    task.MarkAsError(DownloadErrorCode.ChecksumMismatch, "Downloaded size does not match Content-Length.");
                    await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            task.Status = DownloadStatus.Completed;
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
            _logger.Info("Download completed.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_COMPLETED");
        }
        catch (OperationCanceledException)
        {
            if (task.Status != DownloadStatus.Completed && task.Status != DownloadStatus.Error)
            {
                task.Status = DownloadStatus.Paused;
            }

            await SaveStateAsync(task, cancellationToken, ignoreCancellation: true).ConfigureAwait(false);
            _logger.Info("Download canceled.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_CANCELED");
        }
        catch (HttpRequestException httpEx)
        {
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
            var code = ex switch
            {
                UnauthorizedAccessException => DownloadErrorCode.DiskPermissionDenied,
                IOException => DownloadErrorCode.DiskNoSpace,
                _ => DownloadErrorCode.Unknown
            };

            task.MarkAsError(code, ex.Message);
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
            _logger.Info("Download finished.", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_FINISHED");
        }
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

    private static string ResolveFileName(string? suggestedFileName, Uri resourceUri)
    {
        if (!string.IsNullOrWhiteSpace(suggestedFileName))
        {
            return suggestedFileName.Trim();
        }

        var candidate = Path.GetFileName(resourceUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "download.bin";
        }

        return candidate;
    }
}
