using System;
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
    private string _etaText = string.Empty;
    private string _modeText = string.Empty;
    private string _errorText = string.Empty;
    private long _lastBytes;
    private DateTime _lastUpdateTimeUtc;
    private DownloadStatus _status;
    private string _filePath = string.Empty;
    private bool _isFileAvailable;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFromTask(DownloadTask task)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var isSameTask = _id == task.Id;

        Id = task.Id;
        Name = string.IsNullOrWhiteSpace(task.FileName) ? task.Url : task.FileName;
        Status = task.Status;
        if (task.Status == DownloadStatus.Error && task.LastErrorCode != DownloadErrorCode.None)
        {
            StatusText = $"{task.Status} ({task.LastErrorCode})";
        }
        else
        {
            StatusText = task.Status.ToString();
        }

        ErrorText = task.LastErrorMessage ?? string.Empty;
        FilePath = task.SavePath ?? string.Empty;
        IsFileAvailable = !string.IsNullOrWhiteSpace(task.SavePath) && File.Exists(task.SavePath);

        var writtenBytes = task.BytesWritten;

        double progress = 0d;
        double? progressPercent = null;
        if (task.ContentLength.HasValue && task.ContentLength.Value > 0)
        {
            progress = 100.0 * writtenBytes / task.ContentLength.Value;
            progressPercent = progress;
        }

        Progress = progress;
        ProgressPercent = progressPercent;
        SizeText = $"{FormatBytes(writtenBytes)} / {FormatTotal(task.ContentLength)}";
        var nowUtc = DateTime.UtcNow;
        var totalBytes = writtenBytes;

        if (!isSameTask)
        {
            _lastUpdateTimeUtc = default;
            _lastBytes = 0;
        }

        if (_lastUpdateTimeUtc == default)
        {
            _lastUpdateTimeUtc = nowUtc;
            _lastBytes = totalBytes;
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }
        else
        {
            var deltaBytes = totalBytes - _lastBytes;
            var deltaSeconds = (nowUtc - _lastUpdateTimeUtc).TotalSeconds;

            if (deltaSeconds > 0.5 && deltaBytes >= 0)
            {
                var bytesPerSecond = deltaBytes / deltaSeconds;
                SpeedText = FormatSpeed(bytesPerSecond);

                if (task.ContentLength.HasValue &&
                    task.ContentLength.Value > 0 &&
                    bytesPerSecond > 0 &&
                    task.Status == DownloadStatus.Downloading)
                {
                    var remaining = task.ContentLength.Value - totalBytes;
                    if (remaining > 0)
                    {
                        var eta = TimeSpan.FromSeconds(remaining / bytesPerSecond);
                        EtaText = FormatEta(eta);
                    }
                    else
                    {
                        EtaText = string.Empty;
                    }
                }
                else
                {
                    EtaText = string.Empty;
                }

                _lastUpdateTimeUtc = nowUtc;
                _lastBytes = totalBytes;
            }
        }

        if (task.Status == DownloadStatus.Completed)
        {
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }
        else if (task.Status == DownloadStatus.Error)
        {
            SpeedText = string.Empty;
            EtaText = string.Empty;
        }

        ModeText = task.Mode.ToString();
    }

    private static string FormatTotal(long? total)
    {
        return total.HasValue && total.Value > 0
            ? FormatBytes(total.Value)
            : "Unknown";
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
}
