using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Core.Domain;
using SharpDownloadManager.Core.Utilities;
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
        IReadOnlyDictionary<string, string>? extraHeaders = null,
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
                        extraHeaders,
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
                var contentDispositionName = ExtractFileNameFromContentDisposition(
                    response.Content.Headers.ContentDisposition);

                var info = new HttpResourceInfo
                {
                    Url = uri,
                    ContentLength = contentLength,
                    SupportsRange = supportsRange,
                    ETag = response.Headers.ETag?.Tag,
                    LastModified = lastModified,
                    IsChunkedWithoutLength = isChunkedWithoutLength,
                    ContentDispositionFileName = contentDispositionName,
                    ContentType = response.Content.Headers.ContentType?.MediaType
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
                        info.LastModified,
                        info.ContentDispositionFileName
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

    public async Task<DownloadResponseMetadata> DownloadRangeToStreamAsync(
        Uri url,
        long? from,
        long? to,
        Stream target,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowHtmlFallback = true,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));
        if (target is null) throw new ArgumentNullException(nameof(target));

        var ctx = new { Url = url.ToString(), From = from, To = to };

        _logger.Info(
            "Starting range download",
            eventCode: "DOWNLOAD_RANGE_START",
            context: ctx);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyCommonHeaders(request, url, extraHeaders);
        ApplyExtraHeaders(request, extraHeaders);

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

            var metadata = CreateDownloadResponseMetadata(response, from.HasValue || to.HasValue);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            var buffer = new byte[81_920];
            long totalBytesRead = 0;

            var firstRead = await responseStream
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);

            var isHtmlResponse =
                mediaType is not null &&
                mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) &&
                !url.AbsolutePath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) &&
                !url.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

            if (firstRead > 0 && isHtmlResponse)
            {
                var snippetLength = Math.Min(firstRead, 4096);
                var snippet = Encoding.UTF8.GetString(buffer, 0, snippetLength);

                var isFullDownload = !from.HasValue && !to.HasValue;

                if (allowHtmlFallback && isFullDownload &&
                    TryExtractDownloadUrlFromHtml(snippet, url, out var fallbackUrl) &&
                    fallbackUrl is not null)
                {
                    _logger.Info(
                        "HTML fallback extracted a file URL from page.",
                        eventCode: "DOWNLOAD_HTML_FALLBACK",
                        context: new
                        {
                            OriginalUrl = url.ToString(),
                            FallbackUrl = fallbackUrl.ToString()
                        });

                    responseStream.Dispose();
                    response.Dispose();

                    var fallbackMetadata = await DownloadRangeToStreamAsync(
                            fallbackUrl,
                            null,
                            null,
                            target,
                            progress,
                            cancellationToken,
                            allowHtmlFallback: false,
                            extraHeaders: extraHeaders)
                        .ConfigureAwait(false);

                    return fallbackMetadata;
                }

                var safeSnippet = snippet.Length > 1000 ? snippet[..1000] : snippet;

                _logger.Error(
                    "Server returned HTML instead of file.",
                    eventCode: "DOWNLOAD_HTML_RESPONSE",
                    context: new
                    {
                        Url = url.ToString(),
                        Snippet = safeSnippet
                    });

                var message =
                    "Server returned an HTML page instead of the requested file. " +
                    "This usually means the link requires login, a browser session, " +
                    "or an extra confirmation step." + Environment.NewLine +
                    "HTML snippet:" + Environment.NewLine +
                    safeSnippet;

                var ex = new HttpRequestException(message);
                ex.Data["CustomErrorCode"] = DownloadErrorCode.HtmlResponse;
                throw ex;
            }

            if (firstRead > 0)
            {
                await target
                    .WriteAsync(buffer.AsMemory(0, firstRead), cancellationToken)
                    .ConfigureAwait(false);

                progress?.Report(firstRead);
                totalBytesRead += firstRead;
            }

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
                totalBytesRead += read;
            }

            _logger.Info(
                "Completed range download",
                eventCode: "DOWNLOAD_RANGE_SUCCESS",
                context: ctx);

            if (!from.HasValue && !to.HasValue && totalBytesRead > 0)
            {
                if (!metadata.ResourceLength.HasValue || metadata.ResourceLength.Value <= 0)
                {
                    var responseLength = metadata.ResponseContentLength;
                    var resolvedResponseLength = responseLength.HasValue && responseLength.Value > 0
                        ? responseLength.Value
                        : totalBytesRead;

                    metadata = metadata with
                    {
                        ResourceLength = totalBytesRead,
                        ResponseContentLength = resolvedResponseLength
                    };
                }
            }

            return metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.Info(
                "Range download canceled by request.",
                eventCode: "DOWNLOAD_RANGE_CANCELED",
                context: new
                {
                    ctx.Url,
                    From = ctx.From,
                    To = ctx.To,
                    CancellationRequested = true
                });

            throw;
        }
        catch (OperationCanceledException ex)
        {
            var timeoutEx = new TimeoutException(
                "The download operation timed out or the remote host closed the connection.",
                ex);

            _logger.Error(
                "Range download canceled unexpectedly (timeout).",
                eventCode: "DOWNLOAD_RANGE_ERROR",
                exception: timeoutEx,
                context: ctx);

            throw timeoutEx;
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            var status = ex.StatusCode.Value;
            var friendly = GetFriendlyHttpErrorMessage(status);
            var wrapped = new HttpRequestException(friendly, ex, status);

            foreach (var key in ex.Data.Keys.Cast<object>().ToArray())
            {
                wrapped.Data[key] = ex.Data[key];
            }

            _logger.Error(
                "Range download HTTP error",
                eventCode: "DOWNLOAD_RANGE_HTTP_ERROR",
                exception: wrapped,
                context: new
                {
                    ctx.Url,
                    Status = (int)status,
                    Reason = status.ToString()
                });

            throw wrapped;
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
        catch (Exception ex) when (ex is not TimeoutException)
        {
            _logger.Error(
                "Range download failed",
                eventCode: "DOWNLOAD_RANGE_ERROR",
                exception: ex,
                context: ctx);

            throw;
        }
    }

    private static void ApplyCommonHeaders(
        HttpRequestMessage request,
        Uri uri,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        try
        {
            static bool HasHeader(IReadOnlyDictionary<string, string>? headers, string name)
            {
                if (headers is null)
                {
                    return false;
                }

                foreach (var key in headers.Keys)
                {
                    if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (!HasHeader(extraHeaders, "User-Agent"))
            {
                request.Headers.UserAgent.Clear();
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/120.0.0.0 Safari/537.36");
            }

            if (!HasHeader(extraHeaders, "Accept"))
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("*/*");
            }

            if (!HasHeader(extraHeaders, "Accept-Language"))
            {
                request.Headers.AcceptLanguage.Clear();
                request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            }

            if (uri.Host.EndsWith("gofile.io", StringComparison.OrdinalIgnoreCase) &&
                !uri.Host.Equals("gofile.io", StringComparison.OrdinalIgnoreCase))
            {
                // Some hosts (e.g. store-eu-par-*.gofile.io) expect the main gofile.io referrer.
                request.Headers.Referrer = new Uri("https://gofile.io/");
            }
            else
            {
                if (!HasHeader(extraHeaders, "Referer"))
                {
                    var origin = new Uri($"{uri.Scheme}://{uri.Host}/");
                    request.Headers.Referrer = origin;
                }
            }
        }
        catch
        {
            // ignore invalid referrer
        }
    }

    private static void ApplyExtraHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? extraHeaders)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (extraHeaders is null)
        {
            return;
        }

        static bool ShouldSkipHeader(string headerName)
        {
            return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Expect", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var kvp in extraHeaders)
        {
            var headerName = kvp.Key?.Trim();
            if (string.IsNullOrWhiteSpace(headerName))
            {
                continue;
            }

            if (ShouldSkipHeader(headerName))
            {
                // Host and Content-Length are managed by HttpClient, Range is managed by downloader.
                continue;
            }

            var headerValue = kvp.Value ?? string.Empty;

            if (headerName.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(headerValue, UriKind.Absolute, out var referer))
                {
                    request.Headers.Referrer = referer;
                }

                continue;
            }

            if (request.Headers.Contains(headerName))
            {
                request.Headers.Remove(headerName);
            }

            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }
    }

    private static DownloadResponseMetadata CreateDownloadResponseMetadata(
        HttpResponseMessage response,
        bool isRangeRequest)
    {
        if (response is null)
        {
            throw new ArgumentNullException(nameof(response));
        }

        var supportsRange = response.Headers.AcceptRanges.Contains("bytes") ||
                            response.Content.Headers.ContentRange is not null;

        long? resourceLength = null;
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is not null && contentRange.Length.HasValue)
        {
            resourceLength = contentRange.Length.Value;
        }
        else if (!isRangeRequest)
        {
            resourceLength = response.Content.Headers.ContentLength;
        }

        var contentDispositionName = ExtractFileNameFromContentDisposition(
            response.Content.Headers.ContentDisposition);

        return new DownloadResponseMetadata(
            response.Content.Headers.ContentLength,
            resourceLength,
            supportsRange,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified,
            contentDispositionName,
            response.Content.Headers.ContentType?.MediaType);
    }

    // HTML helper regexes for fallback download URL extraction
    private static readonly Regex MetaRefreshRegex = new(
        @"<meta\s+http-equiv\s*=\s*[""']refresh[""'][^>]*url=(?<url>[^""'>\s]+)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnchorLinkRegex = new(
        @"<a\s+[^>]*href\s*=\s*[""'](?<url>[^""'#>]+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    // Keywords we look for inside the anchor text to decide if it's a likely download link
    private static readonly string[] AnchorKeywords =
    {
        "download",
        "click here",
        "direct link",
        "download now",
        "start download",
        "get file"
    };

    private static readonly HashSet<string> LikelyFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip",
        ".rar",
        ".7z",
        ".tar",
        ".gz",
        ".tgz",
        ".iso",
        ".exe",
        ".msi",
        ".mp4",
        ".mkv",
        ".avi",
        ".mov",
        ".mp3",
        ".flac",
        ".wav",
        ".pdf",
        ".epub"
    };

    private static bool TryExtractDownloadUrlFromHtml(
        string htmlSnippet,
        Uri originalUrl,
        out Uri? downloadUrl)
    {
        downloadUrl = null;

        if (string.IsNullOrWhiteSpace(htmlSnippet))
        {
            return false;
        }

        if (TryExtractFromMetaRefresh(htmlSnippet, originalUrl, out downloadUrl))
        {
            return true;
        }

        if (TryExtractFromAnchors(htmlSnippet, originalUrl, out downloadUrl))
        {
            return true;
        }

        downloadUrl = null;
        return false;
    }

    private static string? ExtractFileNameFromContentDisposition(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
        {
            return null;
        }

        return FileNameHelper.NormalizeFileName(disposition.FileNameStar)
            ?? FileNameHelper.NormalizeFileName(disposition.FileName);
    }

    private static bool TryExtractFromMetaRefresh(string htmlSnippet, Uri originalUrl, out Uri? downloadUrl)
    {
        downloadUrl = null;

        foreach (Match match in MetaRefreshRegex.Matches(htmlSnippet))
        {
            var candidateString = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(candidateString))
            {
                continue;
            }

            candidateString = candidateString.Trim('\'', '"');

            if (!TryBuildCandidateUrl(candidateString, originalUrl, out var candidate))
            {
                continue;
            }

            if (candidate is not null &&
                !IsSameResource(candidate, originalUrl) &&
                IsCandidateLikelyFile(candidate, originalUrl))
            {
                downloadUrl = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractFromAnchors(string htmlSnippet, Uri originalUrl, out Uri? downloadUrl)
    {
        downloadUrl = null;

        foreach (Match match in AnchorLinkRegex.Matches(htmlSnippet))
        {
            var href = match.Groups["url"].Value;
            if (!TryBuildCandidateUrl(href, originalUrl, out var candidate) || candidate is null)
            {
                continue;
            }

            var anchorText = match.Groups["text"].Value;
            var anchorTag = match.Value;

            var isLikelyDownload = anchorTag.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isLikelyDownload)
            {
                foreach (var keyword in AnchorKeywords)
                {
                    if (anchorText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        isLikelyDownload = true;
                        break;
                    }
                }
            }

            if (!isLikelyDownload)
            {
                continue;
            }

            if (IsSameResource(candidate, originalUrl))
            {
                continue;
            }

            if (!IsCandidateLikelyFile(candidate, originalUrl))
            {
                continue;
            }

            downloadUrl = candidate;
            return true;
        }

        return false;
    }

    private static bool IsSameResource(Uri candidate, Uri original)
    {
        return Uri.Compare(
                   candidate,
                   original,
                   UriComponents.HttpRequestUrl,
                   UriFormat.Unescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool TryBuildCandidateUrl(string href, Uri originalUrl, out Uri? candidateUrl)
    {
        candidateUrl = null;

        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        href = href.Trim();

        if (href.Length == 0 ||
            href.StartsWith("#", StringComparison.Ordinal) ||
            href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
        {
            candidateUrl = absolute;
            return true;
        }

        if (Uri.TryCreate(originalUrl, href, out var relative))
        {
            candidateUrl = relative;
            return true;
        }

        return false;
    }

    private static bool IsCandidateLikelyFile(Uri candidate, Uri originalUrl)
    {
        if (candidate is null)
        {
            return false;
        }

        if (!(string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var extension = Path.GetExtension(candidate.AbsolutePath);
        if (string.IsNullOrEmpty(extension) || !LikelyFileExtensions.Contains(extension))
        {
            return false;
        }

        if (candidate.Host.Equals(originalUrl.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidate.Host.EndsWith("." + originalUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            originalUrl.Host.EndsWith("." + candidate.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task<HttpResponseMessage> SendWithFallbackAsync(
        Uri uri,
        HttpMethod primaryMethod,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(primaryMethod, uri);
        ApplyCommonHeaders(request, uri, extraHeaders);
        ApplyExtraHeaders(request, extraHeaders);

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
            ApplyCommonHeaders(getRequest, uri, extraHeaders);
            ApplyExtraHeaders(getRequest, extraHeaders);

            return await _client
                .SendAsync(
                    getRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }

    private static string GetFriendlyHttpErrorMessage(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.Forbidden =>
                "Server returned 403 Forbidden. This usually means the link requires a browser session, login, or an extra confirmation step.",
            HttpStatusCode.NotFound =>
                "Server returned 404 Not Found. The file link is invalid or has expired.",
            HttpStatusCode.TooManyRequests =>
                "Server returned 429 Too Many Requests. You may need to wait or slow down your downloads.",
            HttpStatusCode.InternalServerError =>
                "Server returned 500 Internal Server Error.",
            HttpStatusCode.ServiceUnavailable =>
                "Server returned 503 Service Unavailable. The service may be temporarily down.",
            _ =>
                $"Server returned HTTP {(int)statusCode} ({statusCode})."
        };
    }
}
