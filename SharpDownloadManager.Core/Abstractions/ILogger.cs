using System;

namespace SharpDownloadManager.Core.Abstractions;

public interface ILogger
{
    void Info(string message, Guid? downloadId = null, string? eventCode = null, object? context = null);

    void Warn(string message, Guid? downloadId = null, string? eventCode = null, object? context = null);

    void Error(string message, Guid? downloadId = null, string? eventCode = null, Exception? exception = null, object? context = null);
}
