using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SharpDownloadManager.UI.ViewModels;

public class MainViewModel
{
    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = new();

    public ICommand NewDownloadCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SettingsCommand { get; }

    public MainViewModel()
    {
        NewDownloadCommand = new RelayCommand(OnNewDownload);
        ResumeCommand = new RelayCommand(OnResume);
        PauseCommand = new RelayCommand(OnPause);
        DeleteCommand = new RelayCommand(OnDelete);
        OpenFileCommand = new RelayCommand(OnOpenFile);
        OpenFolderCommand = new RelayCommand(OnOpenFolder);
        SettingsCommand = new RelayCommand(OnSettings);
    }

    private void OnNewDownload()
    {
        // TODO: Implement new download workflow.
    }

    private void OnResume()
    {
        // TODO: Implement resume functionality.
    }

    private void OnPause()
    {
        // TODO: Implement pause functionality.
    }

    private void OnDelete()
    {
        // TODO: Implement delete functionality.
    }

    private void OnOpenFile()
    {
        // TODO: Implement open file functionality.
    }

    private void OnOpenFolder()
    {
        // TODO: Implement open folder functionality.
    }

    private void OnSettings()
    {
        // TODO: Implement settings workflow.
    }
}
