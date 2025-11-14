using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Utilities;
using SharpDownloadManager.Infrastructure.Logging;

namespace SharpDownloadManager.UI.Services;

/// <summary>
/// Lightweight HTTP bridge that accepts download requests from the browser extension
/// and enqueues them in the download engine.
/// </summary>
public sealed class BrowserBridgeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IBrowserDownloadCoordinator _coordinator;
    private readonly ILogger _logger;
    private readonly HttpListener _listener;

    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private bool _isRunning;
    private bool _disposed;

    public BrowserBridgeServer(IBrowserDownloadCoordinator coordinator, ILogger logger)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:5454/");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || _isRunning)
        {
            return Task.CompletedTask;
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.Warn(
                "Failed to start browser bridge listener. Browser integration disabled.",
                eventCode: "BROWSER_BRIDGE_START_FAILED",
                context: new { ex.ErrorCode, ex.Message });
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _processingTask = Task.Run(() => ProcessLoopAsync(token), CancellationToken.None);
        _isRunning = true;

        _logger.Info("Browser bridge started.", eventCode: "BROWSER_BRIDGE_STARTED");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_disposed || !_isRunning)
        {
            return;
        }

        _isRunning = false;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        try
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
        }
        catch
        {
        }

        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listener.Close();
        _cts?.Dispose();
        _cts = null;
        _processingTask = null;

        _logger.Info("Browser bridge stopped.", eventCode: "BROWSER_BRIDGE_STOPPED");
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // 995/0x3E3 indicates the listener was stopped during a blocking call.
                if (ex.ErrorCode == 995)
                {
                    break;
                }

                _logger.Warn(
                    "Browser bridge listener error.",
                    eventCode: "BROWSER_BRIDGE_LISTENER_ERROR",
                    context: new { ex.ErrorCode, ex.Message });

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (context is null)
            {
                continue;
            }

            await HandleContextAsync(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        using var response = context.Response;
        try
        {
            var request = context.Request;
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(request.Url?.AbsolutePath, "/api/downloads", StringComparison.Ordinal))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            BrowserDownloadRequest? payload;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8, leaveOpen: false))
            {
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                try
                {
                    payload = JsonSerializer.Deserialize<BrowserDownloadRequest>(body, JsonOptions);
                }
                catch (JsonException)
                {
                    payload = null;
                }
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Url) ||
                !Uri.TryCreate(payload.Url, UriKind.Absolute, out var url))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await WriteJsonAsync(
                        response,
                        new { handled = false, error = "Invalid download request." },
                        cancellationToken)
                    .ConfigureAwait(false);
                _logger.Warn(
                    "Browser API received invalid payload.",
                    eventCode: "BROWSER_API_REQUEST_INVALID");
                return;
            }

            if (!string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                var message = url.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase)
                    ? "This link is a browser-only blob: URL. Let the browser handle the download."
                    : $"IDMFree can only handle HTTP/HTTPS links. The {url.Scheme}: scheme must be downloaded by the browser.";

                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteJsonAsync(
                        response,
                        new
                        {
                            handled = false,
                            status = "fallback",
                            error = message,
                            statusCode = (int)HttpStatusCode.OK
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.Info(
                    "Browser download skipped due to unsupported URL scheme.",
                    eventCode: "BROWSER_API_REQUEST_UNSUPPORTED_SCHEME",
                    context: new { payload.Url, Scheme = url.Scheme });
                return;
            }

            payload.FileName = FileNameHelper.NormalizeFileName(payload.FileName);
            if (string.IsNullOrWhiteSpace(payload.Method))
            {
                payload.Method = "GET";
            }

            _logger.Info(
                "Browser API request received.",
                eventCode: "BROWSER_API_REQUEST_RECEIVED",
                context: new { payload.Url, payload.Method });

            var result = await _coordinator
                .HandleAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            response.StatusCode = (int)result.StatusCode;

            if (result.Success && result.Task is not null)
            {
                await WriteJsonAsync(
                        response,
                        new
                        {
                            handled = result.Handled,
                            status = result.Status,
                            id = result.Task.Id,
                            statusCode = (int)result.StatusCode
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.Info(
                    "Browser API download queued.",
                    eventCode: "BROWSER_API_DOWNLOAD_QUEUED",
                    downloadId: result.Task.Id,
                    context: new { payload.Url });
                return;
            }

            if (result.UserDeclined)
            {
                await WriteJsonAsync(
                        response,
                        new
                        {
                            handled = result.Handled,
                            status = result.Status,
                            error = result.Error ?? "User declined the download.",
                            statusCode = (int)result.StatusCode
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.Info(
                    "Browser download request declined by user.",
                    eventCode: "BROWSER_API_DOWNLOAD_DECLINED",
                    context: new { payload.Url });
                return;
            }

            await WriteJsonAsync(
                    response,
                    new
                    {
                        handled = result.Handled,
                        status = result.Status,
                        error = result.Error ?? "Failed to process download request.",
                        statusCode = (int)result.StatusCode
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Error(
                "Failed to process browser download request.",
                eventCode: "BROWSER_API_DOWNLOAD_FAILED",
                context: new { payload.Url, result.Error, Status = (int)result.StatusCode });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            await WriteJsonAsync(
                    response,
                    new { handled = false, error = "Unexpected server error." },
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.Error(
                "Unexpected error in browser bridge handler.",
                eventCode: "BROWSER_BRIDGE_HANDLER_ERROR",
                exception: ex);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
    }
}
