using System.Collections.Generic;

namespace SharpDownloadManager.Core.Abstractions;

public sealed class BrowserDownloadRequest
{
    public string Url { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Method { get; set; }

    public string? CorrelationId { get; set; }

    public byte[]? Body { get; set; }

    public string? BodyContentType { get; set; }
}
