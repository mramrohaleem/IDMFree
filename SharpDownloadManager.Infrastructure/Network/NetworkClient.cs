using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;
using SharpDownloadManager.Infrastructure.Logging;

namespace SharpDownloadManager.Infrastructure.Network;

public sealed class NetworkClient : INetworkClient
{
    private readonly ILogger _logger;
    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        // Browser-like defaults
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36");

        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        return client;
    }

    public NetworkClient(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HttpResourceInfo> ProbeAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must not be empty.", nameof(url));
        }

        var uri = new Uri(url);

        _logger.Info(
            "Starting probe",
            eventCode: "PROBE_START",
            context: new { Url = url });

        HttpResponseMessage? response = null;

        try
        {
            // Try HEAD first, fall back to GET when servers don't like HEAD.
            response = await SendWithFallbackAsync(
                    uri,
                    HttpMethod.Head,
                    cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
var contentLength = response.Content.Headers.ContentLength;

var supportsRange =
    response.Headers.AcceptRanges.Contains("bytes") ||
    response.Content.Headers.ContentRange != null;

var isChunkedWithoutLength =
    !contentLength.HasValue &&
    response.Headers.TransferEncodingChunked == true;

// نكتفي باللي جاي من Content headers
var lastModified = response.Content.Headers.LastModified;

var info = new HttpResourceInfo
{
    Url = uri,
    ContentLength = contentLength,
    SupportsRange = supportsRange,
    ETag = response.Headers.ETag?.Tag,
    LastModified = lastModified,
    IsChunkedWithoutLength = isChunkedWithoutLength
};

            _logger.Info(
                "Probe completed",
                eventCode: "PROBE_SUCCESS",
                context: new
                {
                    Url = url,
                    info.ContentLength,
                    info.IsChunkedWithoutLength,
                    info.SupportsRange,
                    info.ETag,
                    info.LastModified
                });

            return info;
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            var status = ex.StatusCode.Value;
            string friendly = status switch
            {
                HttpStatusCode.Forbidden =>
                    "Server returned 403 Forbidden. This usually means the link requires a browser session, login, or an extra confirmation step.",
                HttpStatusCode.NotFound =>
                    "Server returned 404 Not Found. The file link is invalid or has expired.",
                HttpStatusCode.TooManyRequests =>
                    "Server returned 429 Too Many Requests. You may need to wait or slow down your downloads.",
                HttpStatusCode.InternalServerError =>
                    "Server returned 500 Internal Server Error.",
                _ =>
                    $"Server returned HTTP {(int)status} ({status})."
            };

            _logger.Error(
                "Probe failed with HTTP error",
                eventCode: "PROBE_HTTP_ERROR",
                exception: ex,
                context: new { Url = url, Status = status });

            throw new HttpRequestException(friendly, ex, status);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(
                "Probe failed with HTTP error",
                eventCode: "PROBE_HTTP_ERROR",
                exception: ex,
                context: new { Url = url });

            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Probe failed with unexpected error",
                eventCode: "PROBE_ERROR",
                exception: ex,
                context: new { Url = url });

            throw;
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task DownloadRangeToStreamAsync(
        Uri url,
        long? from,
        long? to,
        Stream target,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));
        if (target is null) throw new ArgumentNullException(nameof(target));

        var ctx = new { Url = url.ToString(), From = from, To = to };

        _logger.Info(
            "Starting range download",
            eventCode: "DOWNLOAD_RANGE_START",
            context: ctx);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(request, url);

        if (from.HasValue)
        {
            request.Headers.Range = to.HasValue
                ? new RangeHeaderValue(from, to)
                : new RangeHeaderValue(from, null);
        }

        try
        {
            using var response = await _client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;

            // Guard: if server returns HTML instead of a file for a "file-looking" URL.
            if (!from.HasValue &&
                mediaType != null &&
                mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) &&
                !url.AbsolutePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) &&
                !url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                var message =
                    "Server returned an HTML page instead of the requested file. " +
                    "This usually means the link requires login, a browser session, " +
                    "or an extra confirmation step.";
                throw new HttpRequestException(message);
            }

            using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[81_920];

            while (true)
            {
                var read = await responseStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                await target
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(read);
            }

            _logger.Info(
                "Completed range download",
                eventCode: "DOWNLOAD_RANGE_SUCCESS",
                context: ctx);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(
                "Range download HTTP error",
                eventCode: "DOWNLOAD_RANGE_HTTP_ERROR",
                exception: ex,
                context: ctx);

            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(
                "Range download failed",
                eventCode: "DOWNLOAD_RANGE_ERROR",
                exception: ex,
                context: ctx);

            throw;
        }
    }

    private static void ApplyCommonHeaders(HttpRequestMessage request, Uri uri)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        try
        {
            var origin = new Uri($"{uri.Scheme}://{uri.Host}/");
            request.Headers.Referrer = origin;
        }
        catch
        {
            // ignore invalid referrer
        }
    }

    private static async Task<HttpResponseMessage> SendWithFallbackAsync(
        Uri uri,
        HttpMethod primaryMethod,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(primaryMethod, uri);
        ApplyCommonHeaders(request, uri);

        var response = await _client
            .SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed ||
            response.StatusCode == HttpStatusCode.NotImplemented ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            // Some servers reject HEAD but allow GET – retry with GET.
            response.Dispose();

            var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyCommonHeaders(getRequest, uri);

            return await _client
                .SendAsync(
                    getRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }
}
