using System;
using System.Windows;
using SharpDownloadManager.UI.ViewModels;
using SharpDownloadManager.UI.Views;

namespace SharpDownloadManager.UI.Services;

public sealed class SettingsDialogService : ISettingsDialogService
{
    public void ShowSettings(MainViewModel viewModel)
    {
        if (viewModel is null)
        {
            throw new ArgumentNullException(nameof(viewModel));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            throw new InvalidOperationException("Application dispatcher is unavailable.");
        }

        dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow
            {
                Owner = Application.Current?.MainWindow,
                DataContext = viewModel
            };

            window.ShowDialog();
        });
    }
}
