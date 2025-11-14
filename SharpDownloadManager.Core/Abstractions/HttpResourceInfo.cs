using System;

namespace SharpDownloadManager.Core.Abstractions;

public class HttpResourceInfo
{
    public Uri Url { get; init; } = null!;

    public long? ContentLength { get; init; }

    public bool SupportsRange { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// True if the server uses Transfer-Encoding: chunked and does not provide a Content-Length.
    /// </summary>
    public bool IsChunkedWithoutLength { get; init; }

    public string? ContentDispositionFileName { get; init; }
}
