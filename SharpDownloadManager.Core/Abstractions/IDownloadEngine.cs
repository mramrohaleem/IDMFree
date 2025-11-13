using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.Core.Abstractions;

public interface IDownloadEngine
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadTask>> GetAllDownloadsSnapshotAsync(CancellationToken cancellationToken = default);

    Task<DownloadTask> EnqueueDownloadAsync(
        string url,
        string? suggestedFileName,
        string saveFolderPath,
        DownloadMode mode = DownloadMode.Normal,
        CancellationToken cancellationToken = default);

    Task ResumeAsync(Guid downloadId, CancellationToken cancellationToken = default);

    Task PauseAsync(Guid downloadId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid downloadId, CancellationToken cancellationToken = default);
}
