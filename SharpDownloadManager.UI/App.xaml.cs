using System.Windows;
using SharpDownloadManager.Core.Services;
using SharpDownloadManager.Infrastructure.Logging;
using SharpDownloadManager.Infrastructure.Network;
using SharpDownloadManager.Infrastructure.Persistence;
using SharpDownloadManager.UI.ViewModels;
using SharpDownloadManager.UI.Views;

namespace SharpDownloadManager.UI;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logger = new FileLogger();
        var networkClient = new NetworkClient(logger);
        var stateStore = new SqliteDownloadStateStore(logger);
        var engine = new DownloadEngine(networkClient, stateStore, logger);

        await engine.InitializeAsync();

        var mainWindow = new MainWindow
        {
            DataContext = new MainViewModel(engine)
        };

        mainWindow.Show();
    }
}
