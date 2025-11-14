using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;
using SharpDownloadManager.Core.Utilities;
using SharpDownloadManager.Infrastructure.Logging;
using SharpDownloadManager.UI.Views;
using Application = System.Windows.Application;

namespace SharpDownloadManager.UI.Services;

public sealed class BrowserDownloadCoordinator : IBrowserDownloadCoordinator
{
    private readonly IDownloadEngine _downloadEngine;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string> _getDefaultFolder;
    private readonly Action<string> _updateDefaultFolder;

    public BrowserDownloadCoordinator(
        IDownloadEngine downloadEngine,
        ILogger logger,
        Dispatcher dispatcher,
        Func<string> getDefaultFolder,
        Action<string> updateDefaultFolder)
    {
        _downloadEngine = downloadEngine ?? throw new ArgumentNullException(nameof(downloadEngine));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _getDefaultFolder = getDefaultFolder ?? throw new ArgumentNullException(nameof(getDefaultFolder));
        _updateDefaultFolder = updateDefaultFolder ?? throw new ArgumentNullException(nameof(updateDefaultFolder));
    }

    public async Task<BrowserDownloadResult> HandleAsync(BrowserDownloadRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var operation = _dispatcher.InvokeAsync(
            () => ShowDialog(request),
            DispatcherPriority.Normal,
            cancellationToken);

        BrowserDownloadPrompt? prompt = await operation.Task.ConfigureAwait(false);

        if (prompt is null)
        {
            _logger.Info(
                "Browser download dialog dismissed by user.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_CANCELED",
                context: new { request.Url });

            return BrowserDownloadResult.Declined("User canceled the download.");
        }

        _updateDefaultFolder(prompt.TargetFolder);

        IReadOnlyDictionary<string, string>? headers = null;
        if (request.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var task = await _downloadEngine
                .EnqueueDownloadAsync(
                    request.Url,
                    prompt.FileName,
                    prompt.TargetFolder,
                    DownloadMode.Normal,
                    headers,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Info(
                "Browser download dialog confirmed.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_CONFIRMED",
                downloadId: task.Id,
                context: new { request.Url, prompt.TargetFolder, prompt.FileName });

            return BrowserDownloadResult.Accepted(task);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Failed to enqueue download from browser dialog.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_ERROR",
                exception: ex,
                context: new { request.Url });

            return BrowserDownloadResult.Failed(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private BrowserDownloadPrompt? ShowDialog(BrowserDownloadRequest request)
    {
        var owner = Application.Current?.MainWindow;
        BringWindowToFront(owner);

        var defaultFolder = _getDefaultFolder();
        var suggestedFileName = FileNameHelper.NormalizeFileName(request.FileName)
            ?? ExtractFileNameFromUrl(request.Url);

        var dialog = new NewDownloadDialog();
        dialog.Owner = owner;
        dialog.Initialize(request.Url, suggestedFileName, defaultFolder);

        _logger.Info(
            "Browser download dialog shown.",
            eventCode: "BROWSER_DOWNLOAD_DIALOG_SHOWN",
            context: new { request.Url, suggestedFileName, defaultFolder });

        var result = dialog.ShowDialog();
        if (result == true)
        {
            return new BrowserDownloadPrompt(dialog.SelectedFileName, dialog.SelectedFolder);
        }

        return null;
    }

    private static void BringWindowToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
        window.Focus();
    }

    private static string ExtractFileNameFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "download.bin";
        }

        try
        {
            var uri = new Uri(url);
            var fileName = FileNameHelper.NormalizeFileName(Path.GetFileName(uri.AbsolutePath));
            return string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
        }
        catch
        {
            return "download.bin";
        }
    }

    private sealed record BrowserDownloadPrompt(string FileName, string TargetFolder);
}
