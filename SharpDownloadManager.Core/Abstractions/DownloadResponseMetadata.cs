using System;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.Core.Abstractions;

public sealed record DownloadResponseMetadata(
    long? ResponseContentLength,
    long? ResourceLength,
    bool SupportsRange,
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentDispositionFileName,
    string? ContentType,
    string? ContentRangeHeader,
    string? AcceptRangesHeader,
    bool IsChunkedTransfer,
    long? ReportedFileSize,
    string FileSizeSource,
    Uri? FinalResponseUrl);
