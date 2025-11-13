using System;
using System.Collections.Generic;
using System.IO;
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
        }

        if (shouldPersist && task is not null)
        {
            await _stateStore.SaveDownloadAsync(task, cancellationToken).ConfigureAwait(false);
            _logger.Info("Download paused.", downloadId: downloadId, eventCode: "DOWNLOAD_PAUSED");
        }
    }

    public async Task DeleteAsync(Guid downloadId, CancellationToken cancellationToken = default)
    {
        bool removed;
        lock (_syncRoot)
        {
            removed = _downloads.Remove(downloadId);
        }

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

    private Task RunDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        // This will be implemented in a later step (per-chunk download,
        // parallel connections, retry logic, merge, etc.).
        _logger.Info("RunDownloadAsync called (stub).", downloadId: task.Id, eventCode: "DOWNLOAD_RUN_STUB");
        return Task.CompletedTask;
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
