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
    private readonly Dictionary<Guid, ProgressTracker> _progressTrackers = new();
    private readonly string _tempRootFolder;
    private const string RetryAfterSecondsKey = "RetryAfterSeconds";

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
        public TooManyRequestsException(string message, TimeSpan? retryAfter, Exception? innerException = null)
            : base(message, innerException)
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; }
    }

    private sealed class ProgressTracker
    {
        public double? LastLoggedPercent { get; set; }

        public long LastLoggedBytes { get; set; }

        public DateTimeOffset LastLogAt { get; set; }

        public long LastPersistedBytes { get; set; }

        public DateTimeOffset LastPersistedAt { get; set; }
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
                EnsureResumeCapability(task, logChange: false);
                try
                {
                    SyncChunkProgressFromDisk(task, suppressProgressNotification: true);
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        "Failed to synchronize persisted task progress during initialization.",
                        downloadId: task.Id,
                        eventCode: "DOWNLOAD_INIT_SYNC_FAILED",
                        context: new { ex.Message });
                }
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
        string? requestMethod = null,
        string? correlationId = null,
        byte[]? requestBody = null,
        string? requestBodyContentType = null,
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

        var normalizedMethod = ResolveHttpMethod(requestMethod);
        var normalizedBody = requestBody is { Length: > 0 } ? requestBody.ToArray() : null;
        var normalizedBodyContentType = string.IsNullOrWhiteSpace(requestBodyContentType)
            ? null
            : requestBodyContentType.Trim();
        var id = Guid.NewGuid();

        var resourceInfo = await _networkClient
            .ProbeAsync(url, normalizedHeaders, normalizedMethod, cancellationToken)
            .ConfigureAwait(false);

        var normalizedSuggested = FileNameHelper.NormalizeFileName(suggestedFileName);
        var requestedUri = resourceInfo.RequestedUrl ?? new Uri(url);
        var finalUri = resourceInfo.FinalUrl ?? resourceInfo.Url ?? requestedUri;

        var candidateFileName = ResolveFileName(
            normalizedSuggested,
            resourceInfo.ContentDispositionFileName,
            resourceInfo.ContentType,
            finalUri);
        var fileName = EnsureUniqueFileName(saveFolderPath, candidateFileName, id);
        var displayName = SelectDisplayName(
            normalizedSuggested,
            resourceInfo.ContentDispositionFileName,
            fileName);
        var savePath = Path.Combine(saveFolderPath, fileName);
        var tempFolderPath = Path.Combine(_tempRootFolder, id.ToString("N"));

        var session = new DownloadSession
        {
            DownloadId = id,
            RequestedUrl = resourceInfo.RequestedUrl ?? requestedUri,
            NormalizedUrl = resourceInfo.NormalizedUrl ?? resourceInfo.RequestedUrl ?? requestedUri,
            FinalUrl = finalUri,
            HttpMethodUsedForProbe = resourceInfo.ProbeMethod.Method,
            StatusCodeProbe = resourceInfo.ProbeStatusCode,
            ContentLengthHeaderRaw = resourceInfo.ContentLengthHeaderRaw,
            ContentRangeHeaderRaw = resourceInfo.ContentRangeHeaderRaw,
            AcceptRangesHeader = resourceInfo.AcceptRangesHeader,
            ContentType = resourceInfo.ContentType,
            SupportsResume = resourceInfo.SupportsResume,
            IsChunkedTransfer = resourceInfo.IsChunkedTransfer,
            ReportedFileSizeBytes = resourceInfo.ReportedFileSize,
            FileSizeSource = resourceInfo.FileSizeSource,
            TargetDirectory = saveFolderPath,
            PlannedFileName = fileName,
            FinalFileName = displayName,
            FinalFilePath = savePath,
            TemporaryFileName = fileName + ".part",
            BytesDownloadedSoFar = 0
        };

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            _logger.Info(
                "Download created from browser bridge request.",
                downloadId: id,
                eventCode: "DOWNLOAD_CREATED_FROM_BRIDGE",
                context: new
                {
                    CorrelationId = correlationId,
                    RequestedUrl = session.RequestedUrl?.ToString(),
                    FinalUrl = session.FinalUrl?.ToString(),
                    FileName = session.PlannedFileName,
                    DisplayName = session.FinalFileName,
                    ContentType = session.ContentType,
                    ReportedFileSize = session.ReportedFileSizeBytes,
                    HttpMethod = normalizedMethod,
                    ProbeStatus = session.StatusCodeProbe
                });
        }

        _logger.Info(
            "Detected download capabilities.",
            downloadId: id,
            eventCode: "DOWNLOAD_CAPABILITIES_DETECTED",
            context: new
            {
                RequestedUrl = session.RequestedUrl?.ToString(),
                NormalizedUrl = session.NormalizedUrl?.ToString(),
                FinalUrl = session.FinalUrl?.ToString(),
                HttpMethod = session.HttpMethodUsedForProbe,
                session.StatusCodeProbe,
                session.ContentLengthHeaderRaw,
                session.ContentRangeHeaderRaw,
                session.AcceptRangesHeader,
                session.ContentType,
                session.SupportsResume,
                session.IsChunkedTransfer,
                session.FileSizeSource,
                session.ReportedFileSizeBytes,
                PlannedFileName = session.PlannedFileName
            });

        var task = new DownloadTask
        {
            Id = id,
            Url = url,
            FileName = fileName,
            FinalFileName = displayName,
            SavePath = savePath,
            TempChunkFolderPath = tempFolderPath,
            Session = session,
            Status = DownloadStatus.Queued,
            Mode = mode,
            ContentLength = resourceInfo.ContentLength,
            SupportsRange = resourceInfo.SupportsRange,
            ETag = resourceInfo.ETag,
            LastModified = resourceInfo.LastModified,
            RequestHeaders = normalizedHeaders,
            RequestBody = normalizedBody,
            RequestBodyContentType = normalizedBodyContentType,
            ActualFileSize = null,
            RequestMethod = normalizedMethod.Method
        };

        var connectionsCount = DetermineConnectionsCount(mode, resourceInfo);
        task.InitializeChunks(connectionsCount);
        task.MaxParallelConnections = connectionsCount;
        EnsureResumeCapability(task);

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
            context: new
            {
                task.Url,
                task.FileName,
                task.FinalFileName,
                task.Mode,
                task.ContentLength,
                task.SupportsRange,
                task.ResumeCapability,
                RequestMethod = task.RequestMethod,
                session.TargetDirectory,
                PlannedFileName = session.PlannedFileName,
                session.ReportedFileSizeBytes,
                session.FileSizeSource
            });

        return task;
    }

    public async Task ResumeAsync(Guid downloadId, CancellationToken cancellationToken = default)
    {
        DownloadTask? task;
        bool shouldPersist = false;
        bool refreshFromDisk = false;
        bool resetNonResumableProgress = false;
        bool scheduleAfterReset = false;

        lock (_syncRoot)
        {
            if (!_downloads.TryGetValue(downloadId, out task))
            {
                return;
            }

            refreshFromDisk = task.Status != DownloadStatus.Downloading;
        }

        if (refreshFromDisk && task is not null)
        {
            try
            {
                SyncChunkProgressFromDisk(task, suppressProgressNotification: true);
            }
            catch (Exception ex)
            {
                _logger.Warn(
                    "Failed to synchronize progress from disk before resume.",
                    downloadId: downloadId,
                    eventCode: "DOWNLOAD_RESUME_SYNC_FAILED",
                    context: new { ex.Message });
            }
        }

        string? taskUrl = null;
        DownloadResumeCapability resumeCapability = DownloadResumeCapability.Unknown;
        bool supportsRange = false;
        long alreadyDownloaded = 0;
        long? knownLength = null;
        long? nextRangeStart = null;

        lock (_syncRoot)
        {
            if (!_downloads.TryGetValue(downloadId, out task))
            {
                return;
            }

            taskUrl = task.Url;
            resumeCapability = task.ResumeCapability;
            supportsRange = task.SupportsRange;
            alreadyDownloaded = task.TotalDownloadedBytes;
            knownLength = task.ContentLength;
            if (task.Session is not null)
            {
                task.Session.WasResumed = task.Session.WasResumed || alreadyDownloaded > 0;
            }
            if (task.SupportsRange)
            {
                nextRangeStart = CalculateNextRangeStart(task);
            }

            if (task.Status == DownloadStatus.Completed)
            {
                return;
            }

            if (resumeCapability == DownloadResumeCapability.RestartRequired && alreadyDownloaded > 0)
            {
                resetNonResumableProgress = true;
                scheduleAfterReset = true;
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
            else
            {
                shouldPersist = true;
            }

            if (!resetNonResumableProgress)
            {
                TryScheduleDownloads_NoLock();
            }
        }

        if (task is not null)
        {
            _logger.Info(
                "Resume requested.",
                downloadId: downloadId,
                eventCode: "DOWNLOAD_RESUME_REQUESTED",
                context: new
                {
                    Url = taskUrl,
                    ResumeCapability = resumeCapability.ToString(),
                    SupportsRange = supportsRange,
                    AlreadyDownloadedBytes = alreadyDownloaded,
                    KnownContentLength = knownLength,
                    RangeStart = nextRangeStart
                });

            if (resumeCapability == DownloadResumeCapability.RestartRequired)
            {
                _logger.Warn(
                    "Server does not support range requests. Resume will restart from the beginning.",
                    downloadId: downloadId,
                    eventCode: "DOWNLOAD_RESUME_RESTART",
                    context: new
                    {
                        Url = taskUrl,
                        SupportsRange = supportsRange,
                        AlreadyDownloadedBytes = alreadyDownloaded,
                        KnownContentLength = knownLength
                    });
            }
        }

        if (resetNonResumableProgress && task is not null)
        {
            ResetNonResumableProgress(task);
        }

        if (scheduleAfterReset)
        {
            lock (_syncRoot)
            {
                TryScheduleDownloads_NoLock();
            }
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
        ResetProgressTracker(downloadId);

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
        var session = task.Session;
        if (session is not null)
        {
            session.WasResumed = session.WasResumed || task.TotalDownloadedBytes > 0;
            session.TemporaryFileName ??= task.FileName + ".part";
            session.FinalFilePath ??= task.SavePath;
            session.TargetDirectory ??= Path.GetDirectoryName(task.SavePath);
        }

        _logger.Info(
            "Starting download.",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_RUN_START",
            context: new
            {
                RequestedUrl = session?.RequestedUrl?.ToString() ?? task.Url,
                NormalizedUrl = session?.NormalizedUrl?.ToString(),
                FinalUrl = session?.FinalUrl?.ToString(),
                HttpMethodProbe = session?.HttpMethodUsedForProbe ?? task.RequestMethod,
                ProbeStatusCode = session?.StatusCodeProbe,
                session?.ContentLengthHeaderRaw,
                session?.ContentRangeHeaderRaw,
                session?.AcceptRangesHeader,
                session?.ContentType,
                session?.SupportsResume,
                session?.IsChunkedTransfer,
                FileSizeSource = session?.FileSizeSource,
                ReportedFileSizeBytes = session?.ReportedFileSizeBytes ?? task.ContentLength,
                TargetDirectory = session?.TargetDirectory,
                PlannedFileName = session?.PlannedFileName ?? task.FileName,
                task.ConnectionsCount,
                ResumeCapability = task.ResumeCapability.ToString(),
                WasResumed = session?.WasResumed ?? false
            });

        var taskUri = session?.FinalUrl ?? new Uri(task.Url);
        var taskUriLock = new object();
        var requestHttpMethod = ResolveHttpMethod(task.RequestMethod);
        var currentStage = "prepare";

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

            currentStage = "segment-download";
            SyncChunkProgressFromDisk(task);
            task.UpdateProgressFromChunks();
            ReportProgress(task);
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
            currentStage = "merge-segments";
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

            if (task.Session is not null)
            {
                task.Session.BytesDownloadedSoFar = finalLength;
                task.Session.FinalSizeBytes = finalLength;
                task.Session.ReportedFileSizeBytes = finalLength;
                task.Session.FileSizeSource = DownloadFileSizeSource.DiskFinal;
                task.Session.FinalFilePath = task.SavePath;
                task.Session.FinalFileName = task.FileName;
                task.Session.CompletedAt = DateTimeOffset.UtcNow;
            }

            currentStage = "final-validation";
            if (!task.HasKnownContentLength && finalLength > 0)
            {
                task.ContentLength = finalLength;
                _logger.Info(
                    "Content length discovered from finalized file size.",
                    downloadId: task.Id,
                    eventCode: "CONTENT_LENGTH_DISCOVERED_LATE",
                    context: new { task.Url, task.ContentLength });
            }

            ReportProgress(task);

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
                            Actual = finalLength,
                            Stage = currentStage
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
                    context: new { task.Url, Stage = currentStage });

                return;
            }

            task.LastErrorCode = DownloadErrorCode.None;
            task.LastErrorMessage = null;
            task.Status = DownloadStatus.Completed;
            await SaveStateAsync(task, cancellationToken).ConfigureAwait(false);
            var durationSeconds = task.GetActiveSeconds(DateTimeOffset.UtcNow);
            currentStage = "completed";
            _logger.Info(
                "Download completed.",
                downloadId: task.Id,
                eventCode: "DOWNLOAD_RUN_COMPLETED",
                context: new
                {
                    FinalUrl = session?.FinalUrl?.ToString() ?? task.Url,
                    FinalFilePath = task.SavePath,
                    FinalFileName = task.FinalFileName,
                    FinalSizeBytes = finalLength,
                    FilesizeSourceFinal = session?.FileSizeSource,
                    DurationSeconds = durationSeconds,
                    WasResumed = session?.WasResumed ?? false,
                    ConnectionsUsed = task.ConnectionsCount,
                    TotalBytesWritten = task.BytesWritten
                });
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
                    ErrorCode = code.ToString(),
                    Stage = currentStage
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
                    ErrorCode = code.ToString(),
                    Stage = currentStage
                });
        }
        finally
        {
            lock (_syncRoot)
            {
                _cancellationReasons.Remove(task.Id);
                CancelScheduledRetry_NoLock(task.Id);
            }
            ResetProgressTracker(task.Id);
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

                _logger.Warn(
                    "Server does not support range requests. Restarting download from zero.",
                    downloadId: task.Id,
                    eventCode: "DOWNLOAD_CHUNK_RESTART_NO_RANGE",
                    context: new { task.Url });
            }

            if (expectedLength.HasValue && chunk.DownloadedBytes >= expectedLength.Value)
            {
                chunk.Status = ChunkStatus.Completed;
                chunk.LastErrorCode = null;
                chunk.LastErrorMessage = null;
                task.UpdateProgressFromChunks();
                ReportProgress(task);
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
                    ReportProgress(task);
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
                    ReportProgress(task);
                });

                var allowFallback = !from.HasValue && !to.HasValue;
                var attachBody =
                    allowFallback &&
                    chunk.DownloadedBytes == 0 &&
                    task.RequestBody is { Length: > 0 };

                Uri currentTaskUri;
                lock (taskUriLock)
                {
                    currentTaskUri = taskUri;
                }

                var responseMetadata = await _networkClient.DownloadRangeToStreamAsync(
                        currentTaskUri,
                        from,
                        to,
                        fileStream,
                        progress,
                        ct,
                        allowFallback,
                        task.RequestHeaders,
                        requestHttpMethod,
                        attachBody ? task.RequestBody : null,
                        attachBody ? task.RequestBodyContentType : null)
                    .ConfigureAwait(false);

                TryUpdateTaskMetadataFromResponse(task, responseMetadata);

                if (responseMetadata.FinalResponseUrl is Uri updatedUri)
                {
                    lock (taskUriLock)
                    {
                        taskUri = updatedUri;
                    }
                }

                chunk.Status = ChunkStatus.Completed;
                chunk.LastErrorCode = null;
                chunk.LastErrorMessage = null;
                task.UpdateProgressFromChunks();
                ReportProgress(task);
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
                    ReportProgress(task);
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
                ReportProgress(task);
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
                ReportProgress(task);
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                TimeSpan? retryAfter = null;
                if (httpEx.Data.Contains(RetryAfterSecondsKey))
                {
                    var value = httpEx.Data[RetryAfterSecondsKey];
                    if (value is double secondsDouble)
                    {
                        if (secondsDouble > 0)
                        {
                            retryAfter = TimeSpan.FromSeconds(secondsDouble);
                        }
                    }
                    else if (value is string secondsString &&
                             double.TryParse(secondsString, out var parsedSeconds) &&
                             parsedSeconds > 0)
                    {
                        retryAfter = TimeSpan.FromSeconds(parsedSeconds);
                    }
                }

                throw new TooManyRequestsException(httpEx.Message, retryAfter, httpEx);
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
                ReportProgress(task);
                await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                chunk.Status = ChunkStatus.Error;
                chunk.LastErrorCode = DownloadErrorCode.Unknown;
                chunk.LastErrorMessage = ex.Message;
                task.UpdateProgressFromChunks();
                ReportProgress(task);
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

    private void SyncChunkProgressFromDisk(DownloadTask task, bool suppressProgressNotification = false)
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
        if (!suppressProgressNotification)
        {
            ReportProgress(task);
        }
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

        task.TooManyRequestsRetryCount++;
        var attempt = Math.Max(1, task.TooManyRequestsRetryCount);
        var fallbackDelay = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Max(0, attempt - 1))));
        var retryAfter = throttled.RetryAfter;
        if (retryAfter.HasValue && retryAfter.Value <= TimeSpan.Zero)
        {
            retryAfter = null;
        }

        var retryDelay = retryAfter.HasValue
            ? (retryAfter.Value > fallbackDelay ? retryAfter.Value : fallbackDelay)
            : fallbackDelay;

        task.RetryBackoff = retryDelay;
        task.NextRetryUtc = DateTimeOffset.UtcNow + retryDelay;

        var delaySeconds = Math.Max(1, (int)Math.Ceiling(retryDelay.TotalSeconds));

        var baseMessage = string.IsNullOrWhiteSpace(throttled.Message)
            ? "Server returned 429 Too Many Requests."
            : throttled.Message;
        var message = retryDelay > TimeSpan.Zero
            ? $"{baseMessage} Retrying in approximately {delaySeconds} seconds."
            : baseMessage;
        task.LastErrorMessage = message;

        var previousLimit = GetParallelChunkLimit(task);
        var reducedLimit = previousLimit;
        if (previousLimit > 1)
        {
            reducedLimit = Math.Max(1, previousLimit / 2);
            if (reducedLimit == previousLimit)
            {
                reducedLimit = previousLimit - 1;
            }

            reducedLimit = Math.Max(1, reducedLimit);

            if (task.MaxParallelConnections.HasValue)
            {
                task.MaxParallelConnections = Math.Max(1, Math.Min(task.MaxParallelConnections.Value, reducedLimit));
            }
            else
            {
                task.MaxParallelConnections = reducedLimit;
            }

            var newLimit = GetParallelChunkLimit(task);
            if (newLimit < previousLimit)
            {
                _logger.Info(
                    "Reduced parallel chunk limit to respect server throttling.",
                    downloadId: task.Id,
                    eventCode: "DOWNLOAD_PARALLEL_LIMIT_REDUCED",
                    context: new { task.Url, PreviousLimit = previousLimit, NewLimit = newLimit });
            }
        }

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
        ReportProgress(task);

        await SaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);

        _logger.Warn(
            "Download temporarily throttled by remote server (HTTP 429).",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_TOO_MANY_REQUESTS",
            context: new
            {
                task.Url,
                RetryInSeconds = delaySeconds,
                ParallelLimit = GetParallelChunkLimit(task)
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

    private void EnsureResumeCapability(DownloadTask task, bool logChange = true)
    {
        if (task is null)
        {
            return;
        }

        var newCapability = task.SupportsRange
            ? DownloadResumeCapability.Supported
            : DownloadResumeCapability.RestartRequired;

        if (task.Session is not null)
        {
            task.Session.SupportsResume = newCapability == DownloadResumeCapability.Supported;
        }

        if (task.ResumeCapability == newCapability)
        {
            return;
        }

        var previous = task.ResumeCapability;
        task.ResumeCapability = newCapability;

        if (!logChange && previous == DownloadResumeCapability.Unknown)
        {
            return;
        }

        _logger.Info(
            "Resume capability updated.",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_RESUME_CAPABILITY_CHANGED",
            context: new
            {
                task.Url,
                Previous = previous.ToString(),
                Current = newCapability.ToString(),
                task.SupportsRange
            });
    }

    private void ReportProgress(DownloadTask task)
    {
        if (task is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var session = task.Session;
        ProgressTracker tracker;
        lock (_syncRoot)
        {
            if (!_progressTrackers.TryGetValue(task.Id, out tracker))
            {
                tracker = new ProgressTracker
                {
                    LastLoggedBytes = task.TotalDownloadedBytes,
                    LastLoggedPercent = null,
                    LastLogAt = now,
                    LastPersistedAt = now,
                    LastPersistedBytes = task.TotalDownloadedBytes
                };

                if (task.HasKnownContentLength && task.ContentLength is long contentLength && contentLength > 0)
                {
                    tracker.LastLoggedPercent =
                        Math.Clamp((double)task.TotalDownloadedBytes / contentLength * 100d, 0d, 100d);
                }

                _progressTrackers[task.Id] = tracker;
            }
        }

        double? percentForLog = null;
        bool shouldLog = false;
        string? logReason = null;
        bool shouldPersist = false;
        long totalBytes = task.TotalDownloadedBytes;
        long? knownLength = task.ContentLength;

        lock (tracker)
        {
            totalBytes = task.TotalDownloadedBytes;
            if (task.HasKnownContentLength && knownLength.HasValue && knownLength.Value > 0)
            {
                var percent = (double)totalBytes / knownLength.Value * 100d;
                var clamped = Math.Clamp(percent, 0d, 100d);
                percentForLog = Math.Round(percent, 2);

                if (!tracker.LastLoggedPercent.HasValue ||
                    clamped - tracker.LastLoggedPercent.Value >= 5d ||
                    now - tracker.LastLogAt >= TimeSpan.FromSeconds(30) ||
                    clamped >= 100d)
                {
                    shouldLog = true;
                    logReason = "percentage";
                    tracker.LastLoggedPercent = clamped;
                    tracker.LastLoggedBytes = totalBytes;
                    tracker.LastLogAt = now;
                }
            }
            else
            {
                percentForLog = null;
                if (totalBytes - tracker.LastLoggedBytes >= 10L * 1024 * 1024 ||
                    now - tracker.LastLogAt >= TimeSpan.FromSeconds(30))
                {
                    shouldLog = true;
                    logReason = "bytes";
                    tracker.LastLoggedBytes = totalBytes;
                    tracker.LastLogAt = now;
                }
            }

            if (now - tracker.LastPersistedAt >= TimeSpan.FromSeconds(15) ||
                totalBytes - tracker.LastPersistedBytes >= 25L * 1024 * 1024)
            {
                tracker.LastPersistedAt = now;
                tracker.LastPersistedBytes = totalBytes;
                shouldPersist = true;
            }
        }

        if (session is not null)
        {
            session.BytesDownloadedSoFar = totalBytes;
            if (!session.ReportedFileSizeBytes.HasValue && knownLength.HasValue && knownLength.Value > 0)
            {
                session.ReportedFileSizeBytes = knownLength.Value;
            }
        }

        var averageSpeed = task.GetAverageSpeedBytesPerSecond(now);
        TimeSpan? eta = null;
        if (knownLength.HasValue && knownLength.Value > 0 && averageSpeed > 0)
        {
            var remainingBytes = Math.Max(0, knownLength.Value - totalBytes);
            eta = TimeSpan.FromSeconds(remainingBytes / averageSpeed);
        }

        if (shouldLog)
        {
            _logger.Info(
                percentForLog.HasValue
                    ? "Download progress update."
                    : "Download progress update (total size unknown).",
                downloadId: task.Id,
                eventCode: percentForLog.HasValue ? "DOWNLOAD_PROGRESS_PERCENT" : "DOWNLOAD_PROGRESS_BYTES",
                context: new
                {
                    task.Url,
                    DownloadedBytes = totalBytes,
                    ContentLength = knownLength,
                    Percent = percentForLog,
                    task.ResumeCapability,
                    Reason = logReason,
                    SegmentCount = task.Chunks.Count,
                    ReportedTotalSizeBytes = knownLength,
                    CurrentSpeedBytesPerSecond = Math.Round(averageSpeed, 2),
                    EtaSeconds = eta?.TotalSeconds
                });
        }

        if (shouldPersist)
        {
            var saveTask = SaveStateAsync(task, CancellationToken.None);
            _ = saveTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is not null)
                    {
                        _logger.Warn(
                            "Failed to persist download progress.",
                            downloadId: task.Id,
                            eventCode: "DOWNLOAD_PROGRESS_PERSIST_FAILED",
                            context: new
                            {
                                task.Url,
                                Error = t.Exception.GetBaseException().Message
                            });
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ResetProgressTracker(Guid downloadId)
    {
        lock (_syncRoot)
        {
            _progressTrackers.Remove(downloadId);
        }
    }

    private static long? CalculateNextRangeStart(DownloadTask task)
    {
        if (task is null || !task.SupportsRange || task.Chunks is null || task.Chunks.Count == 0)
        {
            return null;
        }

        long? min = null;
        foreach (var chunk in task.Chunks)
        {
            if (chunk.Status == ChunkStatus.Completed)
            {
                continue;
            }

            var start = chunk.StartByte + Math.Max(0, chunk.DownloadedBytes);
            if (min is null || start < min.Value)
            {
                min = start;
            }
        }

        return min;
    }

    private void ResetNonResumableProgress(DownloadTask task)
    {
        if (task is null)
        {
            return;
        }

        // Non-range servers require us to start over, so we clear any partial data
        // from previous attempts to avoid corrupting the final file. This makes the
        // restart explicit and keeps the UI aligned with reality.

        var tempFolder = task.TempChunkFolderPath;
        foreach (var chunk in task.Chunks)
        {
            chunk.DownloadedBytes = 0;
            chunk.Status = ChunkStatus.Pending;
            chunk.LastErrorCode = null;
            chunk.LastErrorMessage = null;

            if (!string.IsNullOrWhiteSpace(tempFolder))
            {
                var chunkPath = Path.Combine(tempFolder, $"{chunk.Index}.part");
                try
                {
                    if (File.Exists(chunkPath))
                    {
                        File.Delete(chunkPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(
                        "Failed to delete non-resumable chunk file during reset.",
                        downloadId: task.Id,
                        eventCode: "DOWNLOAD_NON_RANGE_CHUNK_CLEANUP_FAILED",
                        context: new { chunk.Index, chunkPath, ex.Message });
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(tempFolder))
        {
            TryDeleteDirectory(tempFolder);
        }

        TryDeleteFile(task.SavePath);

        task.ActualFileSize = null;
        task.UpdateProgressFromChunks();
        ResetProgressTracker(task.Id);
        ReportProgress(task);

        _logger.Info(
            "Cleared partial progress because server does not support range resume.",
            downloadId: task.Id,
            eventCode: "DOWNLOAD_NON_RANGE_PROGRESS_RESET",
            context: new { task.Url });
    }

    private void TryRenameSaveFile(string? oldPath, string? newPath, Guid downloadId)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
        {
            return;
        }

        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!File.Exists(oldPath))
        {
            return;
        }

        try
        {
            var newDirectory = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(newDirectory))
            {
                Directory.CreateDirectory(newDirectory);
            }

            if (File.Exists(newPath))
            {
                _logger.Warn(
                    "Skipping file rename because target already exists.",
                    downloadId: downloadId,
                    eventCode: "DOWNLOAD_FILENAME_RENAME_SKIPPED",
                    context: new { OldPath = oldPath, NewPath = newPath });
                return;
            }

            File.Move(oldPath, newPath);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                "Failed to rename download file to updated name.",
                downloadId: downloadId,
                eventCode: "DOWNLOAD_FILENAME_RENAME_FAILED",
                context: new { OldPath = oldPath, NewPath = newPath, Exception = ex });
        }
    }

    private void TryUpdateTaskMetadataFromResponse(DownloadTask task, DownloadResponseMetadata metadata)
    {
        if (task is null || metadata is null)
        {
            return;
        }

        var session = task.Session;
        if (session is not null)
        {
            if (!string.IsNullOrEmpty(metadata.ContentRangeHeader))
            {
                session.ContentRangeHeaderRaw = metadata.ContentRangeHeader;
            }

            if (!string.IsNullOrEmpty(metadata.AcceptRangesHeader))
            {
                session.AcceptRangesHeader = metadata.AcceptRangesHeader;
            }

            if (!string.IsNullOrEmpty(metadata.ContentType))
            {
                session.ContentType = metadata.ContentType;
            }

            session.IsChunkedTransfer = metadata.IsChunkedTransfer;

            if (metadata.FinalResponseUrl is not null)
            {
                session.FinalUrl = metadata.FinalResponseUrl;
            }

            if (metadata.ReportedFileSize.HasValue && metadata.ReportedFileSize.Value > 0)
            {
                session.ReportedFileSizeBytes = metadata.ReportedFileSize;
                if (!string.IsNullOrEmpty(metadata.FileSizeSource))
                {
                    session.FileSizeSource = metadata.FileSizeSource;
                }
            }
            else if (metadata.ResponseContentLength.HasValue && metadata.ResponseContentLength.Value > 0 &&
                     !session.ReportedFileSizeBytes.HasValue)
            {
                session.ReportedFileSizeBytes = metadata.ResponseContentLength.Value;
                if (session.FileSizeSource == DownloadFileSizeSource.Unknown)
                {
                    session.FileSizeSource = DownloadFileSizeSource.HttpHeaders;
                }
            }
        }

        var previouslyKnownLength = task.HasKnownContentLength;
        bool lengthChanged = false;

        if (metadata.ResourceLength.HasValue)
        {
            var length = metadata.ResourceLength.Value;
            if (length > 0 && (!task.ContentLength.HasValue || task.ContentLength.Value != length))
            {
                task.ContentLength = length;
                lengthChanged = true;

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
                lengthChanged = true;

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

        if (lengthChanged && !previouslyKnownLength && task.HasKnownContentLength)
        {
            _logger.Info(
                "Content length discovered from response.",
                downloadId: task.Id,
                eventCode: "CONTENT_LENGTH_DISCOVERED_LATE",
                context: new { task.Url, task.ContentLength });
        }

        var previousSupportsRange = task.SupportsRange;
        task.SupportsRange = metadata.SupportsRange;
        EnsureResumeCapability(task, logChange: previousSupportsRange != task.SupportsRange);

        if (session is not null)
        {
            session.SupportsResume = task.ResumeCapability == DownloadResumeCapability.Supported;
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

                if (!string.IsNullOrEmpty(session?.TargetDirectory))
                {
                    resolvedName = EnsureUniqueFileName(session.TargetDirectory, resolvedName, task.Id);
                }

                if (!string.Equals(task.FileName, resolvedName, StringComparison.Ordinal))
                {
                    var directory = Path.GetDirectoryName(task.SavePath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        var oldFileName = task.FileName;
                        var oldSavePath = task.SavePath;
                        var newSavePath = Path.Combine(directory, resolvedName);

                        _logger.Info(
                            "Updating file name based on server response metadata.",
                            downloadId: task.Id,
                            eventCode: "DOWNLOAD_FILENAME_UPDATED",
                            context: new { Old = oldFileName, New = resolvedName });

                        TryRenameSaveFile(oldSavePath, newSavePath, task.Id);
                        task.FileName = resolvedName;
                        task.SavePath = newSavePath;
                        if (session is not null)
                        {
                            session.PlannedFileName = resolvedName;
                            session.FinalFilePath = newSavePath;
                        }
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

        var normalizedDisplay = FileNameHelper.NormalizeFileName(task.FinalFileName);
        var updatedDisplay = SelectDisplayName(
            normalizedDisplay,
            metadata.ContentDispositionFileName,
            task.FileName);

        if (!string.Equals(task.FinalFileName, updatedDisplay, StringComparison.Ordinal))
        {
            task.FinalFileName = updatedDisplay;
            if (session is not null)
            {
                session.FinalFileName = updatedDisplay;
            }
        }

        if (session is not null && task.ContentLength.HasValue && task.ContentLength.Value > 0)
        {
            session.ReportedFileSizeBytes = task.ContentLength.Value;
            if (!string.IsNullOrEmpty(metadata.FileSizeSource) && metadata.FileSizeSource != DownloadFileSizeSource.Unknown)
            {
                session.FileSizeSource = metadata.FileSizeSource;
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

    private static HttpMethod ResolveHttpMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return HttpMethod.Get;
        }

        try
        {
            var parsed = new HttpMethod(method.Trim().ToUpperInvariant());
            if (parsed == HttpMethod.Get)
            {
                return HttpMethod.Get;
            }

            if (parsed == HttpMethod.Head)
            {
                return HttpMethod.Head;
            }

            return parsed;
        }
        catch
        {
            return HttpMethod.Get;
        }
    }

    private static string SelectDisplayName(
        string? normalizedSuggested,
        string? contentDispositionFileName,
        string resolvedFileName)
    {
        var dispositionName = FileNameHelper.NormalizeContentDispositionFileName(contentDispositionFileName);

        if (!string.IsNullOrWhiteSpace(normalizedSuggested) &&
            !FileNameHelper.LooksLikePlaceholderName(normalizedSuggested))
        {
            return normalizedSuggested;
        }

        if (!string.IsNullOrWhiteSpace(dispositionName) &&
            !FileNameHelper.LooksLikePlaceholderName(dispositionName))
        {
            return dispositionName!;
        }

        if (!FileNameHelper.LooksLikePlaceholderName(resolvedFileName))
        {
            return resolvedFileName;
        }

        return normalizedSuggested ?? dispositionName ?? resolvedFileName;
    }

    private string EnsureUniqueFileName(string directory, string candidate, Guid downloadId)
    {
        var normalized = FileNameHelper.NormalizeFileName(candidate) ?? "download";
        var extension = Path.GetExtension(normalized);
        if (string.IsNullOrEmpty(extension) || extension == ".")
        {
            extension = string.Empty;
        }

        var baseName = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "download";
        }

        var index = 0;
        while (true)
        {
            var candidateName = index == 0
                ? normalized
                : $"{baseName} ({index}){extension}";
            var fullPath = Path.Combine(directory, candidateName);

            var collision = File.Exists(fullPath);
            if (!collision)
            {
                lock (_syncRoot)
                {
                    collision = _downloads.Values.Any(t =>
                        t.Id != downloadId &&
                        string.Equals(t.SavePath, fullPath, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (!collision)
            {
                return candidateName;
            }

            index++;

            if (index > 500)
            {
                return $"{baseName}-{Guid.NewGuid():N}{extension}";
            }
        }
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
        var fromDisposition = FileNameHelper.NormalizeContentDispositionFileName(contentDispositionFileName);
        var fromUrl = FileNameHelper.TryExtractFileNameFromUrl(resourceUri);
        var fromBrowser = FileNameHelper.NormalizeFileName(browserFileName);

        string? candidate = null;

        if (!string.IsNullOrWhiteSpace(fromDisposition) && !FileNameHelper.LooksLikePlaceholderName(fromDisposition))
        {
            candidate = fromDisposition;
        }
        else if (!string.IsNullOrWhiteSpace(fromUrl) && !FileNameHelper.LooksLikePlaceholderName(fromUrl))
        {
            candidate = fromUrl;
        }
        else if (!string.IsNullOrWhiteSpace(fromBrowser) && !FileNameHelper.LooksLikePlaceholderName(fromBrowser))
        {
            candidate = fromBrowser;
        }
        else
        {
            candidate = fromDisposition ?? fromUrl ?? fromBrowser ?? "download";
        }

        candidate ??= "download";

        var extension = Path.GetExtension(candidate);
        var resolvedExtension = TryResolveExtensionFromContentType(contentType);
        var shouldReplaceExtension = string.IsNullOrWhiteSpace(extension) || extension.Equals(".", StringComparison.Ordinal) ||
                                     string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(resolvedExtension) && shouldReplaceExtension)
        {
            var baseName = Path.GetFileNameWithoutExtension(candidate);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "download";
            }

            candidate = baseName + resolvedExtension;
        }
        else if (string.IsNullOrEmpty(extension) || extension == ".")
        {
            candidate = candidate.TrimEnd('.') + (resolvedExtension ?? ".bin");
        }

        return FileNameHelper.NormalizeFileName(candidate) ?? "download";
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
