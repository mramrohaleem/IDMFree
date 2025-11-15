using System;
using System.Collections.Generic;
using System.Globalization;
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
    private const string RetryAfterSecondsKey = "RetryAfterSeconds";

    private sealed class ProbeSnapshot
    {
        public HttpMethod Method { get; init; } = HttpMethod.Head;

        public int StatusCode { get; init; }

        public Uri? FinalUri { get; init; }

        public string? ContentLengthHeader { get; init; }

        public string? ContentRangeHeader { get; init; }

        public string? AcceptRanges { get; init; }

        public string? ContentType { get; init; }

        public string? ContentDispositionFileName { get; init; }

        public long? ContentLength { get; init; }

        public long? ContentRangeLength { get; init; }

        public bool SupportsRange { get; init; }

        public bool IsChunkedTransfer { get; init; }

        public string FileSizeSource { get; init; } = DownloadFileSizeSource.Unknown;

        public long? ReportedFileSize { get; init; }

        public string? ETag { get; init; }

        public DateTimeOffset? LastModified { get; init; }
    }

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
        HttpMethod? preferredMethod = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must not be empty.", nameof(url));
        }

        var requestedUri = new Uri(url);
        var normalizedUri = NormalizeForProbe(requestedUri);

        _logger.Info(
            "Starting probe",
            eventCode: "PROBE_START",
            context: new { Url = url });

        var normalizedMethod = NormalizeHttpMethod(preferredMethod);

        ProbeSnapshot? primarySnapshot = null;
        ProbeSnapshot? rangeSnapshot = null;

        try
        {
            var (response, methodUsed) = await SendWithFallbackAsync(
                    requestedUri,
                    HttpMethod.Head,
                    normalizedMethod == HttpMethod.Head ? HttpMethod.Get : normalizedMethod,
                    extraHeaders,
                    cancellationToken,
                    fallbackUsesRangeProbe: true)
                .ConfigureAwait(false);

            using (response)
            {
                if (!response.IsSuccessStatusCode &&
                    response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var friendly = GetFriendlyHttpErrorMessage(response.StatusCode);
                    var failure = new HttpRequestException(
                        friendly,
                        null,
                        response.StatusCode);

                    throw failure;
                }

                primarySnapshot = CaptureProbeSnapshot(response, methodUsed);

                if ((primarySnapshot.ReportedFileSize is null ||
                     primarySnapshot.FileSizeSource == DownloadFileSizeSource.Unknown) &&
                    methodUsed == HttpMethod.Head)
                {
                    rangeSnapshot = await TryProbeWithRangeAsync(
                            requestedUri,
                            extraHeaders,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            var status = ex.StatusCode.Value;
            _logger.Error(
                "Probe failed with HTTP error",
                eventCode: "PROBE_HTTP_ERROR",
                exception: ex,
                context: new { Url = url, Status = status, Method = normalizedMethod.Method });

            throw new HttpRequestException(ex.Message, ex, status);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(
                "Probe failed with HTTP error",
                eventCode: "PROBE_HTTP_ERROR",
                exception: ex,
                context: new { Url = url, Method = normalizedMethod.Method });

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

        if (primarySnapshot is null)
        {
            throw new HttpRequestException("Failed to retrieve any response headers from the server.");
        }

        var info = BuildHttpResourceInfo(
            requestedUri,
            normalizedUri,
            primarySnapshot,
            rangeSnapshot);

        _logger.Info(
            "Probe completed",
            eventCode: "PROBE_SUCCESS",
            context: new
            {
                RequestedUrl = info.RequestedUrl?.ToString(),
                NormalizedUrl = info.NormalizedUrl?.ToString(),
                FinalUrl = info.FinalUrl?.ToString(),
                ProbeMethod = info.ProbeMethod.Method,
                ProbeStatusCode = info.ProbeStatusCode,
                info.ContentLength,
                info.ReportedFileSize,
                info.FileSizeSource,
                info.ContentLengthHeaderRaw,
                info.ContentRangeHeaderRaw,
                info.AcceptRangesHeader,
                info.SupportsRange,
                info.IsChunkedTransfer,
                info.ContentDispositionFileName,
                info.ContentType
            });

        return info;
    }

    public async Task<DownloadResponseMetadata> DownloadRangeToStreamAsync(
        Uri url,
        long? from,
        long? to,
        Stream target,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowHtmlFallback = true,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        HttpMethod? requestMethod = null)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));
        if (target is null) throw new ArgumentNullException(nameof(target));

        var ctx = new { Url = url.ToString(), From = from, To = to };

        _logger.Info(
            "Starting range download",
            eventCode: "DOWNLOAD_RANGE_START",
            context: ctx);

        var normalizedMethod = NormalizeHttpMethod(requestMethod);
        // Only GET can deliver a response body reliably. If the browser used
        // a different verb (e.g. POST), we still leverage the forwarded
        // headers/cookies but fall back to GET for the actual transfer.
        if (normalizedMethod != HttpMethod.Get)
        {
            normalizedMethod = HttpMethod.Get;
        }

        using var request = new HttpRequestMessage(normalizedMethod, url);
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

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = TryGetRetryAfter(response);
                var friendly429 = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                    ? $"Server returned 429 Too Many Requests. Retrying in approximately {retryAfter.Value.TotalSeconds:0} seconds."
                    : "Server returned 429 Too Many Requests. The server asked us to slow down.";

                var throttled = new HttpRequestException(
                    friendly429,
                    null,
                    HttpStatusCode.TooManyRequests);

                if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
                {
                    throttled.Data[RetryAfterSecondsKey] = retryAfter.Value.TotalSeconds;
                }

                throw throttled;
            }

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
                            extraHeaders: extraHeaders,
                            requestMethod: requestMethod)
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

    private static HttpMethod NormalizeHttpMethod(HttpMethod? method)
    {
        if (method is null)
        {
            return HttpMethod.Get;
        }

        var methodName = method.Method?.Trim();
        if (string.IsNullOrEmpty(methodName))
        {
            return HttpMethod.Get;
        }

        if (methodName.Equals(HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethod.Get;
        }

        if (methodName.Equals(HttpMethod.Head.Method, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethod.Head;
        }

        try
        {
            return new HttpMethod(methodName.ToUpperInvariant());
        }
        catch
        {
            return HttpMethod.Get;
        }
    }

    private static TimeSpan? TryGetRetryAfter(HttpResponseMessage response)
    {
        if (response is null)
        {
            return null;
        }

        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta.HasValue && retryAfter.Delta.Value > TimeSpan.Zero)
            {
                return retryAfter.Delta.Value;
            }

            if (retryAfter.Date.HasValue)
            {
                var delta = retryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (delta > TimeSpan.Zero)
                {
                    return delta;
                }
            }
        }

        return null;
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

        string? contentLengthHeader = null;
        if (response.Content.Headers.TryGetValues("Content-Length", out var lengthValues))
        {
            contentLengthHeader = string.Join(",", lengthValues);
        }

        string? contentRangeHeader = response.Content.Headers.ContentRange?.ToString();
        if (string.IsNullOrEmpty(contentRangeHeader) &&
            response.Headers.TryGetValues("Content-Range", out var rawRange))
        {
            contentRangeHeader = string.Join(",", rawRange);
        }

        string? acceptRangesHeader = null;
        if (response.Headers.TryGetValues("Accept-Ranges", out var ranges))
        {
            acceptRangesHeader = string.Join(",", ranges);
        }

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

        var (reportedSize, sizeSource) = ResolveReportedFileSize(
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentRange?.Length,
            contentLengthHeader,
            contentRangeHeader);

        if (!resourceLength.HasValue || resourceLength.Value <= 0)
        {
            resourceLength = reportedSize;
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
            response.Content.Headers.ContentType?.MediaType,
            contentRangeHeader,
            acceptRangesHeader,
            response.Headers.TransferEncodingChunked == true,
            reportedSize,
            sizeSource);
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

        foreach (var parameter in disposition.Parameters)
        {
            if (string.Equals(parameter.Name, "filename*", StringComparison.OrdinalIgnoreCase))
            {
                var value = parameter.Value?.Trim('"');
                var decodedStar = FileNameHelper.NormalizeContentDispositionFileName(value);
                if (!string.IsNullOrEmpty(decodedStar))
                {
                    return decodedStar;
                }
            }
        }

        var fromStar = FileNameHelper.NormalizeContentDispositionFileName(disposition.FileNameStar);
        if (!string.IsNullOrEmpty(fromStar))
        {
            return fromStar;
        }

        return FileNameHelper.NormalizeContentDispositionFileName(disposition.FileName);
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

    private static async Task<(HttpResponseMessage Response, HttpMethod MethodUsed)> SendWithFallbackAsync(
        Uri uri,
        HttpMethod primaryMethod,
        HttpMethod fallbackMethod,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken,
        bool fallbackUsesRangeProbe)
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
            if (!string.Equals(primaryMethod.Method, fallbackMethod.Method, StringComparison.OrdinalIgnoreCase))
            {
                // Some servers reject HEAD but allow an alternate verb – retry with the fallback method.
                response.Dispose();

                using var fallbackRequest = new HttpRequestMessage(fallbackMethod, uri);
                ApplyCommonHeaders(fallbackRequest, uri, extraHeaders);
                ApplyExtraHeaders(fallbackRequest, extraHeaders);

                if (fallbackUsesRangeProbe && fallbackMethod == HttpMethod.Get)
                {
                    fallbackRequest.Headers.Range = new RangeHeaderValue(0, 0);
                }

                var fallbackResponse = await _client
                    .SendAsync(
                        fallbackRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);

                return (fallbackResponse, fallbackMethod);
            }
        }

        return (response, primaryMethod);
    }

    private static Uri NormalizeForProbe(Uri uri)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private async Task<ProbeSnapshot?> TryProbeWithRangeAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? extraHeaders,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            ApplyCommonHeaders(request, uri, extraHeaders);
            ApplyExtraHeaders(request, extraHeaders);

            using var response = await _client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode &&
                response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                return null;
            }

            return CaptureProbeSnapshot(response, HttpMethod.Get);
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(
                "Range probe fallback failed.",
                eventCode: "PROBE_RANGE_FALLBACK_FAILED",
                context: new { Url = uri.ToString(), ex.Message });
            return null;
        }
    }

    private static ProbeSnapshot CaptureProbeSnapshot(HttpResponseMessage response, HttpMethod method)
    {
        var finalUri = response.RequestMessage?.RequestUri ?? response.Headers.Location ?? response.RequestMessage?.RequestUri;

        string? contentLengthHeader = null;
        if (response.Content.Headers.TryGetValues("Content-Length", out var lengthValues))
        {
            contentLengthHeader = string.Join(",", lengthValues);
        }

        string? acceptRanges = null;
        if (response.Headers.TryGetValues("Accept-Ranges", out var ranges))
        {
            acceptRanges = string.Join(",", ranges);
        }

        string? contentRangeHeader = response.Content.Headers.ContentRange?.ToString();
        if (string.IsNullOrEmpty(contentRangeHeader) &&
            response.Headers.TryGetValues("Content-Range", out var rawContentRange))
        {
            contentRangeHeader = string.Join(",", rawContentRange);
        }

        var contentLength = response.Content.Headers.ContentLength;
        var contentRangeLength = response.Content.Headers.ContentRange?.Length;

        var (reportedSize, sizeSource) = ResolveReportedFileSize(
            contentLength,
            contentRangeLength,
            contentLengthHeader,
            contentRangeHeader);

        var supportsRange = response.Headers.AcceptRanges.Contains("bytes") ||
                            response.Content.Headers.ContentRange != null ||
                            response.StatusCode == HttpStatusCode.PartialContent ||
                            (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && !string.IsNullOrEmpty(contentRangeHeader));

        return new ProbeSnapshot
        {
            Method = method,
            StatusCode = (int)response.StatusCode,
            FinalUri = finalUri,
            ContentLengthHeader = contentLengthHeader,
            ContentRangeHeader = contentRangeHeader,
            AcceptRanges = acceptRanges,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            ContentDispositionFileName = ExtractFileNameFromContentDisposition(response.Content.Headers.ContentDisposition),
            ContentLength = contentLength,
            ContentRangeLength = contentRangeLength,
            SupportsRange = supportsRange,
            IsChunkedTransfer = response.Headers.TransferEncodingChunked == true,
            FileSizeSource = sizeSource,
            ReportedFileSize = reportedSize,
            ETag = response.Headers.ETag?.Tag,
            LastModified = response.Content.Headers.LastModified
        };
    }

    private static HttpResourceInfo BuildHttpResourceInfo(
        Uri requestedUrl,
        Uri normalizedUrl,
        ProbeSnapshot primary,
        ProbeSnapshot? secondary)
    {
        var snapshots = secondary is null
            ? new[] { primary }
            : new[] { primary, secondary };

        ProbeSnapshot? bestSize = snapshots
            .Where(s => s.ReportedFileSize.HasValue && s.ReportedFileSize.Value > 0)
            .OrderBy(s => GetFileSizeSourcePriority(s.FileSizeSource))
            .FirstOrDefault();

        var preferred = bestSize ?? primary;

        long? contentLength = preferred.ContentRangeLength ?? preferred.ContentLength;
        if (!contentLength.HasValue || contentLength.Value <= 0)
        {
            contentLength = preferred.ReportedFileSize;
        }

        var firstNonEmpty = snapshots
            .Select(s => s.ContentLengthHeader)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstContentRange = snapshots
            .Select(s => s.ContentRangeHeader)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstAcceptRanges = snapshots
            .Select(s => s.AcceptRanges)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstContentType = snapshots
            .Select(s => s.ContentType)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstDisposition = snapshots
            .Select(s => s.ContentDispositionFileName)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstEtag = snapshots
            .Select(s => s.ETag)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var firstLastModified = snapshots
            .Select(s => s.LastModified)
            .FirstOrDefault(v => v.HasValue);

        var finalUri = preferred.FinalUri ?? primary.FinalUri ?? normalizedUrl ?? requestedUrl;
        var supportsRange = snapshots.Any(s => s.SupportsRange);
        var isChunked = snapshots.Any(s => s.IsChunkedTransfer);
        var reportedSize = preferred.ReportedFileSize;
        var sizeSource = string.IsNullOrWhiteSpace(preferred.FileSizeSource)
            ? DownloadFileSizeSource.Unknown
            : preferred.FileSizeSource;

        return new HttpResourceInfo
        {
            RequestedUrl = requestedUrl,
            NormalizedUrl = normalizedUrl,
            Url = finalUri,
            FinalUrl = finalUri,
            ProbeMethod = preferred.Method,
            ProbeStatusCode = preferred.StatusCode,
            ContentLengthHeaderRaw = firstNonEmpty,
            ContentRangeHeaderRaw = firstContentRange,
            AcceptRangesHeader = firstAcceptRanges,
            ContentLength = contentLength,
            SupportsRange = supportsRange,
            SupportsResume = supportsRange,
            ETag = firstEtag,
            LastModified = firstLastModified,
            IsChunkedWithoutLength = isChunked && (!contentLength.HasValue || contentLength.Value <= 0),
            ContentDispositionFileName = firstDisposition,
            ContentType = firstContentType,
            IsChunkedTransfer = isChunked,
            ReportedFileSize = reportedSize,
            FileSizeSource = sizeSource
        };
    }

    private static (long? ReportedSize, string Source) ResolveReportedFileSize(
        long? contentLength,
        long? contentRangeLength,
        string? contentLengthRaw,
        string? contentRangeRaw)
    {
        if (contentRangeLength.HasValue && contentRangeLength.Value >= 0)
        {
            return (contentRangeLength.Value, DownloadFileSizeSource.ContentRange);
        }

        if (!string.IsNullOrWhiteSpace(contentRangeRaw))
        {
            var slashIndex = contentRangeRaw.LastIndexOf('/');
            if (slashIndex >= 0 && slashIndex < contentRangeRaw.Length - 1)
            {
                var totalPart = contentRangeRaw[(slashIndex + 1)..];
                if (long.TryParse(totalPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rangeTotal) && rangeTotal > 0)
                {
                    return (rangeTotal, DownloadFileSizeSource.ContentRange);
                }
            }
        }

        if (contentLength.HasValue && contentLength.Value > 0)
        {
            return (contentLength.Value, DownloadFileSizeSource.ContentLength);
        }

        if (!string.IsNullOrWhiteSpace(contentLengthRaw) &&
            long.TryParse(contentLengthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLength) &&
            parsedLength > 0)
        {
            return (parsedLength, DownloadFileSizeSource.HttpHeaders);
        }

        return (null, DownloadFileSizeSource.Unknown);
    }

    private static int GetFileSizeSourcePriority(string? source)
    {
        return source switch
        {
            DownloadFileSizeSource.ContentRange => 0,
            DownloadFileSizeSource.ContentLength => 1,
            DownloadFileSizeSource.HttpHeaders => 2,
            DownloadFileSizeSource.Approximation => 3,
            DownloadFileSizeSource.DiskFinal => 4,
            _ => 5
        };
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
