using SharpDownloadManager.UI.ViewModels;

namespace SharpDownloadManager.UI.Services;

public interface ISettingsDialogService
{
    void ShowSettings(MainViewModel viewModel);
}
