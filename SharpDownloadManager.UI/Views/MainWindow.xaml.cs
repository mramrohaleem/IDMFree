using System.Windows;
using System.Windows.Input;
using SharpDownloadManager.UI.ViewModels;

namespace SharpDownloadManager.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnDownloadsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && viewModel.SelectedDownload is not null)
        {
            if (viewModel.OpenFileCommand.CanExecute(viewModel.SelectedDownload))
            {
                viewModel.OpenFileCommand.Execute(viewModel.SelectedDownload);
            }
        }
    }
}
