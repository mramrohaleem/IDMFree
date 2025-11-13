namespace SharpDownloadManager.Core.Domain;

public class Chunk
{
    public int Index { get; set; }

    public long StartByte { get; set; }

    public long EndByte { get; set; }

    public long DownloadedBytes { get; set; }

    public ChunkStatus Status { get; set; }

    public int RetryCount { get; set; }

    public DownloadErrorCode? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }
}
