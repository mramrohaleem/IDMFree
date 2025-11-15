using System;

namespace SharpDownloadManager.Core.Domain;

public class DownloadSession
{
    public Guid DownloadId { get; set; }

    public Uri? RequestedUrl { get; set; }

    public Uri? NormalizedUrl { get; set; }

    public Uri? FinalUrl { get; set; }

    public string HttpMethodUsedForProbe { get; set; } = "GET";

    public int? StatusCodeProbe { get; set; }

    public string? ContentLengthHeaderRaw { get; set; }

    public string? ContentRangeHeaderRaw { get; set; }

    public string? AcceptRangesHeader { get; set; }

    public string? ContentType { get; set; }

    public bool SupportsResume { get; set; }

    public bool IsChunkedTransfer { get; set; }

    public long? ReportedFileSizeBytes { get; set; }

    public string FileSizeSource { get; set; } = DownloadFileSizeSource.Unknown;

    public long BytesDownloadedSoFar { get; set; }

    public long? FinalSizeBytes { get; set; }

    public string? TemporaryFileName { get; set; }

    public string? PlannedFileName { get; set; }

    public string? FinalFileName { get; set; }

    public string? FinalFilePath { get; set; }

    public string? TargetDirectory { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public bool WasResumed { get; set; }
}
