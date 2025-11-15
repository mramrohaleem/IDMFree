using System;
using System.Net.Http;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.Core.Abstractions;

public class HttpResourceInfo
{
    public Uri RequestedUrl { get; init; } = null!;

    public Uri? NormalizedUrl { get; init; }

    public Uri Url { get; init; } = null!;

    public Uri? FinalUrl { get; init; }

    public HttpMethod ProbeMethod { get; init; } = HttpMethod.Head;

    public int? ProbeStatusCode { get; init; }

    public string? ContentLengthHeaderRaw { get; init; }

    public string? ContentRangeHeaderRaw { get; init; }

    public string? AcceptRangesHeader { get; init; }

    public long? ContentLength { get; init; }

    public bool SupportsRange { get; init; }

    public bool SupportsResume { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>
    /// True if the server uses Transfer-Encoding: chunked and does not provide a Content-Length.
    /// </summary>
    public bool IsChunkedWithoutLength { get; init; }

    public string? ContentDispositionFileName { get; init; }

    public string? ContentType { get; init; }

    public bool IsChunkedTransfer { get; init; }

    public long? ReportedFileSize { get; init; }

    public string FileSizeSource { get; init; } = DownloadFileSizeSource.Unknown;
}
