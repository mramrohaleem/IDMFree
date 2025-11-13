using System;
using System.Collections.Generic;

namespace SharpDownloadManager.Core.Domain;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string SavePath { get; set; } = string.Empty;

    public string TempFolderPath { get; set; } = string.Empty;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public DownloadMode Mode { get; set; } = DownloadMode.Normal;

    public long? ContentLength { get; set; }

    public bool SupportsRange { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    public long TotalDownloadedBytes { get; private set; }

    public int ConnectionsCount { get; private set; }

    public List<Chunk> Chunks { get; } = new();

    public DownloadErrorCode LastErrorCode { get; private set; } = DownloadErrorCode.None;

    public string? LastErrorMessage { get; private set; }

    public void InitializeChunks(int desiredConnectionsCount)
    {
        Chunks.Clear();

        int connections = Math.Max(1, desiredConnectionsCount);

        if (!ContentLength.HasValue || ContentLength.Value <= 0)
        {
            var chunk = new Chunk
            {
                Index = 0,
                StartByte = 0,
                EndByte = -1,
                DownloadedBytes = 0,
                Status = ChunkStatus.Pending,
                RetryCount = 0,
                LastErrorCode = null,
                LastErrorMessage = null
            };

            Chunks.Add(chunk);
            ConnectionsCount = 1;
            TotalDownloadedBytes = 0;
            return;
        }

        long length = ContentLength.Value;
        if (length < connections)
        {
            connections = (int)length;
            if (connections <= 0)
            {
                connections = 1;
            }
        }

        long baseChunkSize = length / connections;
        long remainder = length % connections;

        long start = 0;
        for (int i = 0; i < connections; i++)
        {
            long size = baseChunkSize + (i < remainder ? 1 : 0);
            long end = size > 0 ? start + size - 1 : start - 1;

            var chunk = new Chunk
            {
                Index = i,
                StartByte = start,
                EndByte = end,
                DownloadedBytes = 0,
                Status = ChunkStatus.Pending,
                RetryCount = 0,
                LastErrorCode = null,
                LastErrorMessage = null
            };

            Chunks.Add(chunk);
            start = end + 1;
        }

        ConnectionsCount = Chunks.Count;
        TotalDownloadedBytes = 0;
    }

    public void UpdateProgressFromChunks()
    {
        long total = 0;
        foreach (var chunk in Chunks)
        {
            total += chunk.DownloadedBytes;
        }

        TotalDownloadedBytes = total;
    }

    public void MarkAsError(DownloadErrorCode code, string message)
    {
        Status = DownloadStatus.Error;
        LastErrorCode = code;
        LastErrorMessage = message;
    }
}
