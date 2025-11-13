using System;
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

        try
        {
            var logger = new FileLogger();
            logger.Info("Application starting", eventCode: "APP_START");

            var networkClient = new NetworkClient(logger);
            var stateStore = new SqliteDownloadStateStore(logger);
            var engine = new DownloadEngine(networkClient, stateStore, logger);

            await engine.InitializeAsync();

            logger.Info("Engine initialized - creating main window", eventCode: "APP_INIT_OK");

            var mainWindow = new MainWindow
            {
                DataContext = new MainViewModel(engine)
            };

            mainWindow.Show();
        }
        catch (Exception ex)
        {
            // Show the error so the app doesn't just exit silently
            MessageBox.Show(
                ex.ToString(),
                "SharpDownloadManager fatal error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            try
            {
                var fallbackLogger = new FileLogger();
                fallbackLogger.Error("Fatal error during App.OnStartup", eventCode: "APP_START_FATAL", exception: ex);
            }
            catch
            {
                // Ignore logging errors
            }

            Shutdown(-1);
        }
    }
}
