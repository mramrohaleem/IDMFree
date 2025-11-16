using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SharpDownloadManager.Core.Abstractions;

namespace SharpDownloadManager.Infrastructure.Logging;

public sealed class FileLogger : ILogger
{
    private const long RotationThresholdBytes = 5 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    private readonly string _logFilePath;
    private readonly object _syncRoot = new();

    public FileLogger()
    {
        var baseFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var logFolder = Path.Combine(baseFolder, "SharpDownloadManager", "logs");
        Directory.CreateDirectory(logFolder);
        _logFilePath = Path.Combine(logFolder, "app.log");
    }

    public void Info(string message, Guid? downloadId = null, string? eventCode = null, object? context = null)
        => WriteLog("INFO", message, downloadId, eventCode, context: context);

    public void Warn(
        string message,
        Guid? downloadId = null,
        string? eventCode = null,
        Exception? exception = null,
        object? context = null)
    {
        var contextWithException = CombineContextWithException(context, exception);
        WriteLog("WARN", message, downloadId, eventCode, context: contextWithException);
    }

    public void Error(string message, Guid? downloadId = null, string? eventCode = null, Exception? exception = null, object? context = null)
        => WriteLog(
            "ERROR",
            message,
            downloadId,
            eventCode,
            context: CombineContextWithException(context, exception));

    private static object? CombineContextWithException(object? context, Exception? exception)
    {
        if (exception is null)
        {
            return context;
        }

        var exceptionInfo = new
        {
            ExceptionType = exception.GetType().FullName,
            exception.Message,
            exception.StackTrace
        };

        return context is null
            ? exceptionInfo
            : new { Context = context, Exception = exceptionInfo };
    }

    private void WriteLog(string level, string message, Guid? downloadId, string? eventCode, object? context)
    {
        var timestamp = DateTime.UtcNow.ToString("o");
        var builder = new StringBuilder();
        builder.Append(timestamp)
            .Append(' ')
            .Append('[')
            .Append(level)
            .Append(']');

        if (downloadId.HasValue)
        {
            builder.Append(' ').Append("downloadId=").Append(downloadId.Value);
        }

        if (!string.IsNullOrWhiteSpace(eventCode))
        {
            builder.Append(' ').Append("event=").Append(eventCode);
        }

        builder.Append(' ').Append(message);

        if (context is not null)
        {
            builder.Append(" | context=");
            builder.Append(SerializeContext(context));
        }

        var logLine = builder.ToString();

        lock (_syncRoot)
        {
            RotateIfNeeded();
            File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
        }
    }

    private string SerializeContext(object context)
    {
        try
        {
            return JsonSerializer.Serialize(context, SerializerOptions);
        }
        catch
        {
            return context.ToString() ?? string.Empty;
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath))
            {
                return;
            }

            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length <= RotationThresholdBytes)
            {
                return;
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var directory = Path.GetDirectoryName(_logFilePath) ?? string.Empty;
            var archivePath = Path.Combine(directory, $"app_{timestamp}.log");

            File.Move(_logFilePath, archivePath, overwrite: false);
        }
        catch
        {
            // Swallow exceptions from rotation to avoid crashing the application due to logging issues.
        }
    }
}
