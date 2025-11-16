namespace SharpDownloadManager.Core.Domain;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Throttled,
    Completed,
    Error,
    Merging,
    Verifying
}

public enum DownloadMode
{
    Normal,
    SafeMode
}

public enum ChunkStatus
{
    Pending,
    Downloading,
    Paused,
    Throttled,
    Completed,
    Error
}

public enum DownloadErrorCode
{
    None,
    NetUnreachable,
    ServerError,
    Http4xx,
    Http5xx,
    Http429,
    ContentLengthChanged,
    RangeNotSupported,
    ChunkedNoLength,
    DiskNoSpace,
    DiskPermissionDenied,
    MergeFailed,
    ChecksumMismatch,
    StateStoreCorrupted,
    Unknown,
    HtmlResponse,
    RequiresBrowser,
    ValidationMismatch,
    ZeroBytes
}

public enum HttpStatusCategory
{
    None = 0,
    ClientError,
    ServerError,
    TooManyRequests
}

public enum DownloadResumeCapability
{
    Unknown = 0,
    RestartRequired,
    Supported
}
