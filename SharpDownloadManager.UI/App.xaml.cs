using System.Windows;
using SharpDownloadManager.Core.Services;
using SharpDownloadManager.Infrastructure.Logging;
using SharpDownloadManager.Infrastructure.Network;
using SharpDownloadManager.Infrastructure.Persistence;
using SharpDownloadManager.UI.ViewModels;
using SharpDownloadManager.UI.Views;
using SharpDownloadManager.UI.Services;

namespace SharpDownloadManager.UI;

public partial class App : Application
{
    private BrowserBridgeServer? _browserBridge;
    private INotificationService? _notificationService;

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

            var notificationService = new NotificationService(logger);
            _notificationService = notificationService;

            var settingsDialogService = new SettingsDialogService();

            var mainViewModel = new MainViewModel(engine, logger, notificationService, settingsDialogService);

            var mainWindow = new MainWindow
            {
                DataContext = mainViewModel
            };

            mainWindow.Show();

            var browserCoordinator = new BrowserDownloadCoordinator(
                engine,
                logger,
                Dispatcher,
                () => mainViewModel.SaveFolderPath,
                folder => mainViewModel.SaveFolderPath = folder);

            var browserBridge = new BrowserBridgeServer(browserCoordinator, logger);
            try
            {
                await browserBridge.StartAsync();
                _browserBridge = browserBridge;
            }
            catch (Exception bridgeEx)
            {
                logger.Warn(
                    "Failed to start browser bridge.",
                    eventCode: "BROWSER_BRIDGE_UNHANDLED_START_ERROR",
                    context: new { bridgeEx.Message });
            }
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

    protected override void OnExit(ExitEventArgs e)
    {
        _browserBridge?.Dispose();
        _notificationService?.Dispose();
        base.OnExit(e);
    }
}
