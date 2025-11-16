using System;
using System.Collections.Generic;
using System.Net.Http;

namespace SharpDownloadManager.Core.Domain;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string FinalFileName { get; set; } = string.Empty;

    public string SavePath { get; set; } = string.Empty;

    public string TempChunkFolderPath { get; set; } = string.Empty;

    public DownloadSession Session { get; set; } = new();

    public string RequestMethod { get; set; } = HttpMethod.Get.Method;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public DownloadMode Mode { get; set; } = DownloadMode.Normal;

    public long? ContentLength { get; set; }

    public bool SupportsRange { get; set; }

    public DownloadResumeCapability ResumeCapability { get; set; } = DownloadResumeCapability.Unknown;

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public long TotalDownloadedBytes { get; set; }

    public long BytesWritten { get; set; }

    public long? ActualFileSize { get; set; }

    public int ConnectionsCount { get; set; }

    public int? MaxParallelConnections { get; set; }

    public TimeSpan TotalActiveTime { get; set; }

    public DateTimeOffset? LastResumedAt { get; set; }

    public HttpStatusCategory HttpStatusCategory { get; set; } = HttpStatusCategory.None;

    public int TooManyRequestsRetryCount { get; set; }

    public TimeSpan RetryBackoff { get; set; } = TimeSpan.Zero;

    public DateTimeOffset? NextRetryUtc { get; set; }

    public List<Chunk> Chunks { get; set; } = new List<Chunk>();

    public DownloadErrorCode LastErrorCode { get; set; } = DownloadErrorCode.None;

    public string? LastErrorMessage { get; set; }

    public IReadOnlyDictionary<string, string>? RequestHeaders { get; set; }

    public byte[]? RequestBody { get; set; }

    public string? RequestBodyContentType { get; set; }

    public bool HasKnownContentLength => ContentLength.HasValue && ContentLength.Value > 0;

    public bool ResumeSupported => ResumeCapability == DownloadResumeCapability.Supported;

    public bool ResumeRestartsFromZero => ResumeCapability == DownloadResumeCapability.RestartRequired;

    public void InitializeChunks(int desiredConnectionsCount)
    {
        Chunks.Clear();

        if (desiredConnectionsCount <= 0)
        {
            desiredConnectionsCount = 1;
        }

        if (!ContentLength.HasValue || ContentLength.Value <= 0)
        {
            Chunks.Add(new Chunk
            {
                Index = 0,
                StartByte = 0,
                EndByte = -1,
                DownloadedBytes = 0,
                Status = ChunkStatus.Pending,
                RetryCount = 0,
                LastErrorCode = null,
                LastErrorMessage = null
            });

            ConnectionsCount = 1;
            TotalDownloadedBytes = 0;
            return;
        }

        long length = ContentLength.Value;
        int chunkCount = Math.Max(1, desiredConnectionsCount);
        if (length < chunkCount)
        {
            chunkCount = (int)length;
            if (chunkCount == 0)
            {
                chunkCount = 1;
            }
        }

        long baseSize = length / chunkCount;
        long remainder = length % chunkCount;
        long startByte = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            long chunkSize = baseSize;
            if (remainder > 0)
            {
                chunkSize += 1;
                remainder -= 1;
            }

            long endByte = startByte + chunkSize - 1;
            if (i == chunkCount - 1)
            {
                endByte = length - 1;
            }

            Chunks.Add(new Chunk
            {
                Index = i,
                StartByte = startByte,
                EndByte = endByte,
                DownloadedBytes = 0,
                Status = ChunkStatus.Pending,
                RetryCount = 0,
                LastErrorCode = null,
                LastErrorMessage = null
            });

            startByte = endByte + 1;
        }

        ConnectionsCount = Chunks.Count;
        TotalDownloadedBytes = 0;
        BytesWritten = 0;
        TotalActiveTime = TimeSpan.Zero;
        LastResumedAt = null;
        TooManyRequestsRetryCount = 0;
        RetryBackoff = TimeSpan.Zero;
        NextRetryUtc = null;
        HttpStatusCategory = HttpStatusCategory.None;
        MaxParallelConnections = null;
    }

    public void UpdateProgressFromChunks()
    {
        long total = 0;
        foreach (var chunk in Chunks)
        {
            total += chunk.DownloadedBytes;
        }

        TotalDownloadedBytes = total;
        BytesWritten = total;
    }

    public void MarkAsError(DownloadErrorCode code, string message)
    {
        Status = DownloadStatus.Error;
        LastErrorCode = code;
        LastErrorMessage = message;
    }

    public void MarkDownloadResumed(DateTimeOffset? resumeTimestamp = null)
    {
        var now = resumeTimestamp ?? DateTimeOffset.UtcNow;
        if (!LastResumedAt.HasValue)
        {
            LastResumedAt = now;
        }
    }

    public void MarkDownloadSuspended(DateTimeOffset? suspendTimestamp = null)
    {
        if (!LastResumedAt.HasValue)
        {
            return;
        }

        var now = suspendTimestamp ?? DateTimeOffset.UtcNow;
        if (now < LastResumedAt.Value)
        {
            LastResumedAt = null;
            return;
        }

        TotalActiveTime += now - LastResumedAt.Value;
        LastResumedAt = null;
    }

    public double GetAverageSpeedBytesPerSecond(DateTimeOffset? now = null)
    {
        var totalSeconds = GetActiveSeconds(now);
        if (totalSeconds <= 0)
        {
            return 0d;
        }

        return TotalDownloadedBytes / totalSeconds;
    }

    public double GetActiveSeconds(DateTimeOffset? now = null)
    {
        var seconds = TotalActiveTime.TotalSeconds;
        if (LastResumedAt.HasValue)
        {
            var reference = now ?? DateTimeOffset.UtcNow;
            if (reference > LastResumedAt.Value)
            {
                seconds += (reference - LastResumedAt.Value).TotalSeconds;
            }
        }

        return seconds;
    }
}
