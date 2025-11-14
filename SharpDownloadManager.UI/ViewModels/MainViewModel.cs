using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;
using SharpDownloadManager.UI.Services;
using MessageBox = System.Windows.MessageBox;

namespace SharpDownloadManager.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IDownloadEngine _engine;
    private readonly ILogger _logger;
    private readonly INotificationService _notificationService;
    private readonly ISettingsDialogService _settingsDialogService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly AsyncCommand _addDownloadCommand;
    private readonly AsyncCommand<DownloadItemViewModel> _pauseDownloadCommand;
    private readonly AsyncCommand<DownloadItemViewModel> _resumeDownloadCommand;
    private readonly AsyncCommand<DownloadItemViewModel> _deleteDownloadCommand;
    private readonly AsyncCommand<DownloadItemViewModel> _openFileCommand;
    private readonly AsyncCommand<DownloadItemViewModel> _openFolderCommand;
    private readonly RelayCommand _openSettingsCommand;
    private readonly HashSet<Guid> _notifiedCompletedDownloads = new();
    private string _newDownloadUrl = string.Empty;
    private string _saveFolderPath = string.Empty;
    private DownloadItemViewModel? _selectedDownload;
    private bool _areCompletionNotificationsEnabled = true;

    public ObservableCollection<DownloadItemViewModel> Downloads { get; }

    public string NewDownloadUrl
    {
        get => _newDownloadUrl;
        set
        {
            if (SetProperty(ref _newDownloadUrl, value))
            {
                _addDownloadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SaveFolderPath
    {
        get => _saveFolderPath;
        set => SetProperty(ref _saveFolderPath, value);
    }

    public DownloadItemViewModel? SelectedDownload
    {
        get => _selectedDownload;
        set
        {
            if (SetProperty(ref _selectedDownload, value))
            {
                _pauseDownloadCommand.RaiseCanExecuteChanged();
                _resumeDownloadCommand.RaiseCanExecuteChanged();
                _deleteDownloadCommand.RaiseCanExecuteChanged();
                _openFileCommand.RaiseCanExecuteChanged();
                _openFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand AddDownloadCommand => _addDownloadCommand;

    public ICommand PauseDownloadCommand => _pauseDownloadCommand;

    public ICommand ResumeDownloadCommand => _resumeDownloadCommand;

    public ICommand DeleteDownloadCommand => _deleteDownloadCommand;

    public ICommand OpenFileCommand => _openFileCommand;

    public ICommand OpenFolderCommand => _openFolderCommand;

    public ICommand OpenSettingsCommand => _openSettingsCommand;

    public bool AreCompletionNotificationsEnabled
    {
        get => _areCompletionNotificationsEnabled;
        set => SetProperty(ref _areCompletionNotificationsEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
        : this(
            new DesignTimeDownloadEngine(),
            new DesignTimeLogger(),
            new NoopNotificationService(),
            new DesignTimeSettingsDialogService())
    {
    }

    public MainViewModel(
        IDownloadEngine engine,
        ILogger logger,
        INotificationService notificationService,
        ISettingsDialogService settingsDialogService)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _settingsDialogService = settingsDialogService ?? throw new ArgumentNullException(nameof(settingsDialogService));
        Downloads = new ObservableCollection<DownloadItemViewModel>();

        var downloadsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        SaveFolderPath = downloadsFolder;

        _addDownloadCommand = new AsyncCommand(AddDownloadAsync, () => !string.IsNullOrWhiteSpace(NewDownloadUrl));
        _pauseDownloadCommand = new AsyncCommand<DownloadItemViewModel>(PauseDownloadAsync, CanOperateOnItem);
        _resumeDownloadCommand = new AsyncCommand<DownloadItemViewModel>(ResumeDownloadAsync, CanOperateOnItem);
        _deleteDownloadCommand = new AsyncCommand<DownloadItemViewModel>(DeleteDownloadAsync, CanOperateOnItem);
        _openFileCommand = new AsyncCommand<DownloadItemViewModel>(OpenFileAsync, CanOpenFile);
        _openFolderCommand = new AsyncCommand<DownloadItemViewModel>(OpenFolderAsync, CanOpenFile);
        _openSettingsCommand = new RelayCommand(OpenSettings);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshFromEngineAsync();
        _refreshTimer.Start();

        _ = RefreshFromEngineAsync();
    }

    private bool CanOperateOnItem(DownloadItemViewModel? item) => (item ?? SelectedDownload) is not null;

    private bool CanOpenFile(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        return target?.IsFileAvailable ?? false;
    }

    private async Task AddDownloadAsync()
    {
        var url = NewDownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var task = await _engine.EnqueueDownloadAsync(url, null, SaveFolderPath);
            var existing = Downloads.FirstOrDefault(d => d.Id == task.Id);
            if (existing is null)
            {
                Downloads.Add(new DownloadItemViewModel(task));
            }
            else
            {
                existing.UpdateFromTask(task);
            }

            NewDownloadUrl = string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            MessageBox.Show(
                ex.Message,
                "Failed to add download",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task PauseDownloadAsync(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        if (target is null)
        {
            return;
        }

        try
        {
            await _engine.PauseAsync(target.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            MessageBox.Show(
                ex.Message,
                "Download operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ResumeDownloadAsync(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        if (target is null)
        {
            return;
        }

        try
        {
            await _engine.ResumeAsync(target.Id);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            MessageBox.Show(
                ex.Message,
                "Download operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task DeleteDownloadAsync(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        if (target is null)
        {
            return;
        }

        try
        {
            await _engine.DeleteAsync(target.Id);

            var existing = Downloads.FirstOrDefault(d => d.Id == target.Id);
            if (existing is not null)
            {
                if (ReferenceEquals(existing, SelectedDownload))
                {
                    SelectedDownload = null;
                }

                Downloads.Remove(existing);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);

            MessageBox.Show(
                ex.Message,
                "Download operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private Task OpenFileAsync(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        if (target is null)
        {
            return Task.CompletedTask;
        }

        var path = target.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(
                "The downloaded file could not be found. It may have been moved or deleted.",
                "Open File",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Failed to open downloaded file.",
                downloadId: target.Id,
                eventCode: "DOWNLOAD_OPEN_FILE_FAILED",
                exception: ex,
                context: new { target.FilePath });

            MessageBox.Show(
                ex.Message,
                "Open File",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private Task OpenFolderAsync(DownloadItemViewModel? item)
    {
        var target = item ?? SelectedDownload;
        if (target is null)
        {
            return Task.CompletedTask;
        }

        var path = target.FilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(
                "The downloaded file could not be found. It may have been moved or deleted.",
                "Open Containing Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Failed to open containing folder.",
                downloadId: target.Id,
                eventCode: "DOWNLOAD_OPEN_FOLDER_FAILED",
                exception: ex,
                context: new { target.FilePath });

            MessageBox.Show(
                ex.Message,
                "Open Containing Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        return Task.CompletedTask;
    }

    private void OpenSettings()
    {
        try
        {
            _settingsDialogService.ShowSettings(this);
        }
        catch (Exception ex)
        {
            _logger.Warn(
                "Failed to open settings dialog.",
                eventCode: "SETTINGS_DIALOG_ERROR",
                context: new { ex.Message });

            MessageBox.Show(
                ex.Message,
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task RefreshFromEngineAsync()
    {
        try
        {
            var snapshot = await _engine.GetAllDownloadsSnapshotAsync();

            foreach (var task in snapshot)
            {
                var existing = Downloads.FirstOrDefault(d => d.Id == task.Id);
                if (existing is null)
                {
                    existing = new DownloadItemViewModel(task);
                    Downloads.Add(existing);
                }
                else
                {
                    existing.UpdateFromTask(task);
                }

                if (task.Status == DownloadStatus.Completed)
                {
                    if (_notifiedCompletedDownloads.Add(task.Id) && AreCompletionNotificationsEnabled)
                    {
                        try
                        {
                            var targetFolder = string.IsNullOrWhiteSpace(task.SavePath)
                                ? null
                                : Path.GetDirectoryName(task.SavePath);
                            _notificationService.ShowDownloadCompleted(task.FileName, targetFolder);
                        }
                        catch (Exception ex)
                        {
                            _logger.Warn(
                                "Failed to show completion notification.",
                                downloadId: task.Id,
                                eventCode: "DOWNLOAD_NOTIFICATION_ERROR",
                                context: new { ex.Message });
                        }
                    }
                }
                else
                {
                    _notifiedCompletedDownloads.Remove(task.Id);
                }
            }

            var ids = new HashSet<Guid>(snapshot.Select(t => t.Id));
            for (int i = Downloads.Count - 1; i >= 0; i--)
            {
                if (!ids.Contains(Downloads[i].Id))
                {
                    if (ReferenceEquals(Downloads[i], SelectedDownload))
                    {
                        SelectedDownload = null;
                    }

                    Downloads.RemoveAt(i);
                }
            }

            UpdateCommandStates();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void UpdateCommandStates()
    {
        _pauseDownloadCommand.RaiseCanExecuteChanged();
        _resumeDownloadCommand.RaiseCanExecuteChanged();
        _deleteDownloadCommand.RaiseCanExecuteChanged();
        _openFileCommand.RaiseCanExecuteChanged();
        _openFolderCommand.RaiseCanExecuteChanged();
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected virtual void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Func<bool>? _canExecute;

        public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public async void Execute(object? parameter)
        {
            await _execute();
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand<T> : ICommand
    {
        private readonly Func<T?, Task> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public AsyncCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(ConvertParameter(parameter)) ?? true;

        public async void Execute(object? parameter)
        {
            await _execute(ConvertParameter(parameter));
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        private static T? ConvertParameter(object? parameter)
        {
            if (parameter is null)
            {
                return default;
            }

            if (parameter is T value)
            {
                return value;
            }

            return default;
        }
    }

    private sealed class DesignTimeDownloadEngine : IDownloadEngine
    {
        private readonly List<DownloadTask> _tasks = new()
        {
            new DownloadTask
            {
                Id = Guid.NewGuid(),
                Url = "https://example.com/sample1.bin",
                FileName = "sample1.bin",
                Mode = DownloadMode.Normal,
                Status = DownloadStatus.Downloading,
                ContentLength = 1_000_000,
                TotalDownloadedBytes = 250_000,
                BytesWritten = 250_000
            },
            new DownloadTask
            {
                Id = Guid.NewGuid(),
                Url = "https://example.com/sample2.bin",
                FileName = "sample2.bin",
                Mode = DownloadMode.SafeMode,
                Status = DownloadStatus.Paused,
                ContentLength = 500_000,
                TotalDownloadedBytes = 100_000,
                BytesWritten = 100_000
            }
        };

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DownloadTask>> GetAllDownloadsSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DownloadTask>>(_tasks);

        public Task<DownloadTask> EnqueueDownloadAsync(
            string url,
            string? suggestedFileName,
            string saveFolderPath,
            DownloadMode mode = DownloadMode.Normal,
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            CancellationToken cancellationToken = default)
        {
            var resolvedFileName = string.IsNullOrWhiteSpace(suggestedFileName)
                ? "download.bin"
                : suggestedFileName;

            var task = new DownloadTask
            {
                Id = Guid.NewGuid(),
                Url = url,
                FileName = resolvedFileName,
                SavePath = Path.Combine(saveFolderPath, resolvedFileName),
                Mode = mode,
                Status = DownloadStatus.Queued,
                ContentLength = 1_000_000,
                TotalDownloadedBytes = 0,
                BytesWritten = 0,
                RequestHeaders = requestHeaders
            };
            _tasks.Add(task);
            return Task.FromResult(task);
        }

        public Task ResumeAsync(Guid downloadId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PauseAsync(Guid downloadId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid downloadId, CancellationToken cancellationToken = default)
        {
            _tasks.RemoveAll(t => t.Id == downloadId);
            return Task.CompletedTask;
        }
    }

    private sealed class DesignTimeLogger : ILogger
    {
        public void Info(string message, Guid? downloadId = null, string? eventCode = null, object? context = null)
        {
        }

        public void Warn(string message, Guid? downloadId = null, string? eventCode = null, object? context = null)
        {
        }

        public void Error(string message, Guid? downloadId = null, string? eventCode = null, Exception? exception = null, object? context = null)
        {
        }
    }

    private sealed class NoopNotificationService : INotificationService
    {
        public void ShowDownloadCompleted(string fileName, string? targetFolder)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DesignTimeSettingsDialogService : ISettingsDialogService
    {
        public void ShowSettings(MainViewModel viewModel)
        {
        }
    }
}
