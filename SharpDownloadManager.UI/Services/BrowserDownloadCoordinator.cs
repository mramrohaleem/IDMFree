using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
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

        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? null
            : request.CorrelationId;

        _logger.Info(
            "Browser bridge download request received.",
            eventCode: "BROWSER_BRIDGE_REQUEST_HANDLING",
            context: new
            {
                request.Url,
                request.Method,
                CorrelationId = correlationId,
                Headers = request.Headers?.Count ?? 0
            });

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
                context: new { request.Url, CorrelationId = correlationId });

            _logger.Info(
                "Browser bridge download request rejected.",
                eventCode: "DOWNLOAD_REJECTED_FROM_BRIDGE",
                context: new { request.Url, Reason = "UserCanceled", CorrelationId = correlationId });

            return BrowserDownloadResult.Declined("User canceled the download.");
        }

        _updateDefaultFolder(prompt.TargetFolder);

        IReadOnlyDictionary<string, string>? headers = null;
        if (request.Headers is { Count: > 0 })
        {
            headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        }

        var normalizedMethod = NormalizeHttpMethod(request.Method);

        try
        {
            var task = await _downloadEngine
                .EnqueueDownloadAsync(
                    request.Url,
                    prompt.FileName,
                    prompt.TargetFolder,
                    DownloadMode.Normal,
                    headers,
                    normalizedMethod,
                    correlationId,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Info(
                "Browser download dialog confirmed.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_CONFIRMED",
                downloadId: task.Id,
                context: new
                {
                    request.Url,
                    prompt.TargetFolder,
                    prompt.FileName,
                    Method = normalizedMethod,
                    CorrelationId = correlationId
                });

            return BrowserDownloadResult.Accepted(task);
        }
        catch (HttpRequestException httpEx)
        {
            var status = httpEx.StatusCode ?? HttpStatusCode.BadGateway;

            if (!cancellationToken.IsCancellationRequested)
            {
                _dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        httpEx.Message,
                        "Download failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                });
            }

            _logger.Warn(
                "Failed to enqueue download due to HTTP error.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_HTTP_ERROR",
                context: new { request.Url, Status = (int)status, httpEx.Message, CorrelationId = correlationId });

            _logger.Info(
                "Browser bridge download request rejected.",
                eventCode: "DOWNLOAD_REJECTED_FROM_BRIDGE",
                context: new
                {
                    request.Url,
                    Reason = "HttpRequestException",
                    Status = (int)status,
                    CorrelationId = correlationId
                });

            return BrowserDownloadResult.Fallback(status, httpEx.Message);
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Failed to enqueue download from browser dialog.",
                eventCode: "BROWSER_DOWNLOAD_DIALOG_ERROR",
                exception: ex,
                context: new { request.Url, CorrelationId = correlationId });

            _logger.Info(
                "Browser bridge download request rejected.",
                eventCode: "DOWNLOAD_REJECTED_FROM_BRIDGE",
                context: new
                {
                    request.Url,
                    Reason = ex.GetType().Name,
                    CorrelationId = correlationId
                });

            if (!cancellationToken.IsCancellationRequested)
            {
                _dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        ex.Message,
                        "Failed to start download",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                });
            }

            return BrowserDownloadResult.Fallback(HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    private BrowserDownloadPrompt? ShowDialog(BrowserDownloadRequest request)
    {
        var owner = Application.Current?.MainWindow;
        BringWindowToFront(owner);

        var defaultFolder = _getDefaultFolder();

        Uri? requestUri = null;
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var parsedUri))
        {
            requestUri = parsedUri;
        }

        var normalizedSuggested = FileNameHelper.NormalizeFileName(request.FileName);
        var fallbackFromUrl = FileNameHelper.TryExtractFileNameFromUrl(requestUri);
        string? suggestedFileName = normalizedSuggested;

        if (FileNameHelper.LooksLikePlaceholderName(suggestedFileName) &&
            !FileNameHelper.LooksLikePlaceholderName(fallbackFromUrl))
        {
            suggestedFileName = fallbackFromUrl;
        }

        suggestedFileName ??= fallbackFromUrl;
        suggestedFileName ??= ExtractFileNameFromUrl(request.Url);

        var promptMessage = BrowserDownloadSafetyInspector
            .Analyze(requestUri, suggestedFileName);

        var dialog = new NewDownloadDialog();
        dialog.Owner = owner;
        dialog.Initialize(request.Url, suggestedFileName, defaultFolder, promptMessage);

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

    private static string NormalizeHttpMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return HttpMethod.Get.Method;
        }

        try
        {
            var parsed = new HttpMethod(method.Trim());
            return parsed.Method.ToUpperInvariant();
        }
        catch
        {
            return HttpMethod.Get.Method;
        }
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
            var fileName = FileNameHelper.TryExtractFileNameFromUrl(uri);
            return string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
        }
        catch
        {
            return "download.bin";
        }
    }

    private sealed record BrowserDownloadPrompt(string FileName, string TargetFolder);
}
