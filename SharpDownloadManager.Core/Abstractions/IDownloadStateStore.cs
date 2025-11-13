using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.Core.Abstractions;

public interface IDownloadStateStore
{
    /// <summary>
    /// Ensure the underlying storage exists and schema is created.
    /// Safe to call multiple times.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Save or update a single DownloadTask and its chunks as an atomic operation.
    /// </summary>
    Task SaveDownloadAsync(DownloadTask task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load all persisted DownloadTask instances, including their chunks.
    /// Any task that was in Downloading, Merging, or Verifying state
    /// must be reset to Paused so it can be safely resumed.
    /// </summary>
    Task<List<DownloadTask>> LoadAllDownloadsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a download and its chunks from the state store.
    /// </summary>
    Task DeleteDownloadAsync(Guid id, CancellationToken cancellationToken = default);
}
