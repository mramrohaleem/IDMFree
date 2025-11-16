using System;
using System.Collections.Concurrent;
using SharpDownloadManager.Core.Abstractions;

namespace SharpDownloadManager.Infrastructure.Tests;

internal sealed class TestLogger : ILogger
{
    private readonly ConcurrentQueue<string> _messages = new();

    public void Info(string message, Guid? downloadId = null, string? eventCode = null, object? context = null)
    {
        _messages.Enqueue($"INFO:{eventCode}:{message}");
    }

    public void Warn(
        string message,
        Guid? downloadId = null,
        string? eventCode = null,
        Exception? exception = null,
        object? context = null)
    {
        var suffix = exception is null ? string.Empty : $" | {exception.Message}";
        _messages.Enqueue($"WARN:{eventCode}:{message}{suffix}");
    }

    public void Error(string message, Guid? downloadId = null, string? eventCode = null, Exception? exception = null, object? context = null)
    {
        var suffix = exception is null ? string.Empty : $" | {exception.Message}";
        _messages.Enqueue($"ERROR:{eventCode}:{message}{suffix}");
    }

    public string[] DrainMessages()
    {
        return _messages.ToArray();
    }
}
