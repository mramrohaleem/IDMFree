using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.IO;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.UI.ViewModels;

public class DownloadItemViewModel : INotifyPropertyChanged
{
    private Guid _id;
    private string _name = string.Empty;
    private string _statusText = string.Empty;
    private double _progress;
    private double? _progressPercent;
    private string _sizeText = string.Empty;
    private string _speedText = string.Empty;
    private string _averageSpeedText = string.Empty;
    private string _etaText = string.Empty;
    private string _modeText = string.Empty;
    private string _errorText = string.Empty;
    private DownloadStatus _status;
    private string _filePath = string.Empty;
    private bool _isFileAvailable;
    private bool _isProgressIndeterminate;
    private DownloadResumeCapability _resumeCapability;
    private readonly Queue<SpeedSample> _speedSamples = new();
    private const double SpeedWindowSeconds = 3.0;

    public DownloadItemViewModel()
    {
    }

    public DownloadItemViewModel(DownloadTask task)
    {
        UpdateFromTask(task);
    }

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public double? ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public string SizeText
    {
        get => _sizeText;
        set => SetProperty(ref _sizeText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        set => SetProperty(ref _speedText, value);
    }

    public string AverageSpeedText
    {
        get => _averageSpeedText;
        set => SetProperty(ref _averageSpeedText, value);
    }

    public string EtaText
    {
        get => _etaText;
        set => SetProperty(ref _etaText, value);
    }

    public string ModeText
    {
        get => _modeText;
        set => SetProperty(ref _modeText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        set => SetProperty(ref _errorText, value);
    }

    public DownloadStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    public bool IsFileAvailable
    {
        get => _isFileAvailable;
        set => SetProperty(ref _isFileAvailable, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public DownloadResumeCapability ResumeCapability
    {
        get => _resumeCapability;
        set => SetProperty(ref _resumeCapability, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFromTask(DownloadTask task)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var isSameTask = _id == task.Id;

        Id = task.Id;
        var displayName = string.IsNullOrWhiteSpace(task.FinalFileName)
            ? (string.IsNullOrWhiteSpace(task.FileName) ? task.Url : task.FileName)
            : task.FinalFileName;
        Name = displayName;
        Status = task.Status;
        ResumeCapability = task.ResumeCapability;
        if (task.Status == DownloadStatus.Throttled || task.HttpStatusCategory == HttpStatusCategory.TooManyRequests)
        {
            var message = "Throttled by server (HTTP 429";
            if (task.NextRetryUtc.HasValue)
            {
                var retryIn = task.NextRetryUtc.Value - DateTimeOffset.UtcNow;
                if (retryIn > TimeSpan.Zero)
                {
                    message += $", retrying in {FormatEta(retryIn)}";
                }
            }

            message += ")";
            StatusText = message;
        }
        else if (task.Status == DownloadStatus.Error && task.LastErrorCode != DownloadErrorCode.None)
        {
            StatusText = $"{task.Status} ({task.LastErrorCode})";
        }
        else
        {
            StatusText = task.Status.ToString();
        }

        if (task.ResumeRestartsFromZero &&
            task.Status is DownloadStatus.Paused or DownloadStatus.Downloading or DownloadStatus.Queued)
        {
            var note = "Resume will restart from the beginning (server does not support range requests)";
            StatusText = string.IsNullOrWhiteSpace(StatusText)
                ? note
                : $"{StatusText} – {note}";
        }

        ErrorText = task.LastErrorMessage ?? string.Empty;
        FilePath = task.SavePath ?? string.Empty;
        IsFileAvailable = !string.IsNullOrWhiteSpace(task.SavePath) && File.Exists(task.SavePath);

        var writtenBytes = task.BytesWritten < 0 ? 0 : task.BytesWritten;

        long? progressTotal = null;
        if (task.ContentLength.HasValue && task.ContentLength.Value > 0)
        {
            progressTotal = task.ContentLength.Value;
        }
        else if (task.Status == DownloadStatus.Completed)
        {
            if (task.ActualFileSize.HasValue && task.ActualFileSize.Value > 0)
            {
                progressTotal = task.ActualFileSize.Value;
            }
            else if (writtenBytes > 0)
            {
                progressTotal = writtenBytes;
            }
        }

        long? displayTotal = progressTotal;
        if (!displayTotal.HasValue && task.ActualFileSize.HasValue && task.ActualFileSize.Value > 0)
        {
            displayTotal = task.ActualFileSize.Value;
        }

        double progress = 0d;
        double? progressPercent = null;
        if (task.Status == DownloadStatus.Completed)
        {
            progress = 100d;
            progressPercent = 100d;
        }
        else if (progressTotal.HasValue && progressTotal.Value > 0)
        {
            progress = 100.0 * writtenBytes / progressTotal.Value;
            progressPercent = progress;
        }

        var clampedProgress = Math.Clamp(progress, 0d, 100d);
        Progress = clampedProgress;
        ProgressPercent = progressPercent.HasValue ? Math.Clamp(progressPercent.Value, 0d, 100d) : null;
        IsProgressIndeterminate = task.Status == DownloadStatus.Downloading && (!progressTotal.HasValue || progressTotal.Value <= 0);
        if (displayTotal.HasValue && displayTotal.Value > 0)
        {
            SizeText = $"{FormatBytes(writtenBytes)} / {FormatBytes(displayTotal.Value)}";
        }
        else
        {
            SizeText = $"Downloaded {FormatBytes(writtenBytes)} (total size unknown)";
        }

        var averageSpeed = task.GetAverageSpeedBytesPerSecond();
        AverageSpeedText = averageSpeed > 0 ? FormatSpeed(averageSpeed) : string.Empty;

        var nowUtc = DateTime.UtcNow;
        if (!isSameTask)
        {
            _speedSamples.Clear();
        }

        if (task.Status == DownloadStatus.Downloading)
        {
            var sample = new SpeedSample(nowUtc, writtenBytes);
            _speedSamples.Enqueue(sample);

            while (_speedSamples.Count > 0 &&
                   (sample.Timestamp - _speedSamples.Peek().Timestamp).TotalSeconds > SpeedWindowSeconds)
            {
                _speedSamples.Dequeue();
            }

            if (_speedSamples.Count > 1)
            {
                var oldest = _speedSamples.Peek();
                var windowSeconds = (sample.Timestamp - oldest.Timestamp).TotalSeconds;
                var bytesDelta = sample.Bytes - oldest.Bytes;

                if (windowSeconds >= 0.5 && bytesDelta >= 0)
                {
                    var bytesPerSecond = bytesDelta / windowSeconds;
                    SpeedText = FormatSpeed(bytesPerSecond);

                    if (task.ContentLength.HasValue && task.ContentLength.Value > 0 && bytesPerSecond > 0)
                    {
                        var remaining = task.ContentLength.Value - writtenBytes;
                        EtaText = remaining > 0
                            ? FormatEta(TimeSpan.FromSeconds(remaining / bytesPerSecond))
                            : string.Empty;
                    }
                    else
                    {
                        EtaText = string.Empty;
                    }
                }
                else
                {
                    SpeedText = string.Empty;
                    EtaText = string.Empty;
                }
            }
            else
            {
                SpeedText = string.Empty;
                EtaText = string.Empty;
            }
        }
        else
        {
            _speedSamples.Clear();
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }

        ModeText = task.Mode.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "0 B";
        }

        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return string.Empty;
        }

        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        double value = bytesPerSecond;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.0} {units[unitIndex]}";
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalSeconds <= 0 || double.IsInfinity(eta.TotalSeconds) || double.IsNaN(eta.TotalSeconds))
        {
            return string.Empty;
        }

        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}h {eta.Minutes}m";
        }

        if (eta.TotalMinutes >= 1)
        {
            return $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
        }

        return $"{eta.Seconds}s";
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected virtual void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private readonly struct SpeedSample
    {
        public SpeedSample(DateTime timestamp, long bytes)
        {
            Timestamp = timestamp;
            Bytes = bytes;
        }

        public DateTime Timestamp { get; }

        public long Bytes { get; }
    }
}
