namespace SharpDownloadManager.Core.Domain;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
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
    ContentLengthChanged,
    RangeNotSupported,
    ChunkedNoLength,
    DiskNoSpace,
    DiskPermissionDenied,
    MergeFailed,
    ChecksumMismatch,
    StateStoreCorrupted,
    Unknown
}
