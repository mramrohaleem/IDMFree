using System;

namespace SharpDownloadManager.Core.Abstractions;

public sealed record DownloadResponseMetadata(
    long? ResponseContentLength,
    long? ResourceLength,
    bool SupportsRange,
    string? ETag,
    DateTimeOffset? LastModified,
    string? ContentDispositionFileName,
    string? ContentType);
