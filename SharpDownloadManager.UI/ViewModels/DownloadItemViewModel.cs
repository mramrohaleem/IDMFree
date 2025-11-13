using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.UI.ViewModels;

public class DownloadItemViewModel : INotifyPropertyChanged
{
    private Guid _id;
    private string _name = string.Empty;
    private string _statusText = string.Empty;
    private double _progress;
    private string _sizeText = string.Empty;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private string _modeText = string.Empty;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateFromTask(DownloadTask task)
    {
        if (task is null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        Id = task.Id;
        Name = string.IsNullOrWhiteSpace(task.FileName) ? task.Url : task.FileName;
        StatusText = task.Status.ToString();

        double progress = 0d;
        if (task.ContentLength.HasValue && task.ContentLength.Value > 0)
        {
            progress = 100.0 * task.TotalDownloadedBytes / task.ContentLength.Value;
        }

        Progress = progress;
        SizeText = $"{task.TotalDownloadedBytes} / {task.ContentLength?.ToString() ?? "Unknown"}";
        SpeedText = string.Empty;
        EtaText = string.Empty;
        ModeText = task.Mode.ToString();
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
