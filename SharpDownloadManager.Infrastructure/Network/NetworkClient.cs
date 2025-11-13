using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;

namespace SharpDownloadManager.Infrastructure.Network;

public class NetworkClient : INetworkClient
{
    private static readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    static NetworkClient()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
    }

    public NetworkClient(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HttpResourceInfo> ProbeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("URL must be provided", nameof(url));
        }

        _logger.Info("Starting probe", eventCode: "PROBE_START", context: new { Url = url });

        try
        {
            var uri = new Uri(url, UriKind.Absolute);

            long? contentLength = null;
            string? etag = null;
            DateTimeOffset? lastModified = null;
            bool isChunkedWithoutLength = false;

            var headRequest = new HttpRequestMessage(HttpMethod.Head, uri);
            HttpResponseMessage? headResponse = null;
            try
            {
                headResponse = await _httpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (headResponse.IsSuccessStatusCode)
                {
                    ExtractMetadata(headResponse, out contentLength, out etag, out lastModified, out isChunkedWithoutLength);
                }
                else if (headResponse.StatusCode == HttpStatusCode.MethodNotAllowed || headResponse.StatusCode == HttpStatusCode.NotImplemented)
                {
                    headResponse.Dispose();
                    headResponse = null;
                }
                else
                {
                    headResponse.EnsureSuccessStatusCode();
                }
            }
            catch (HttpRequestException)
            {
                headResponse?.Dispose();
                headResponse = null;
            }
            finally
            {
                headRequest.Dispose();
            }

            if (headResponse is null)
            {
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, uri);
                using var getResponse = await _httpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                getResponse.EnsureSuccessStatusCode();
                ExtractMetadata(getResponse, out contentLength, out etag, out lastModified, out isChunkedWithoutLength);
            }
            else
            {
                headResponse.Dispose();
            }

            bool supportsRange = await CheckRangeSupportAsync(uri, cancellationToken).ConfigureAwait(false);

            var result = new HttpResourceInfo
            {
                Url = uri,
                ContentLength = contentLength,
                SupportsRange = supportsRange,
                ETag = etag,
                LastModified = lastModified,
                IsChunkedWithoutLength = isChunkedWithoutLength
            };

            _logger.Info("Probe completed", eventCode: "PROBE_SUCCESS", context: new
            {
                Url = uri.ToString(),
                ContentLength = contentLength,
                IsChunkedWithoutLength = isChunkedWithoutLength,
                SupportsRange = supportsRange,
                ETag = etag,
                LastModified = lastModified
            });

            return result;
        }
        catch (HttpRequestException ex)
        {
            _logger.Error("Probe failed with HTTP error", eventCode: "PROBE_HTTP_ERROR", exception: ex, context: new { Url = url });
            throw;
        }
    }

    public async Task DownloadRangeToStreamAsync(
        Uri url,
        long? from,
        long? to,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            throw new ArgumentException("The 'from' value must be less than or equal to the 'to' value.");
        }

        _logger.Info("Starting range download", eventCode: "DOWNLOAD_RANGE_START", context: new { Url = url.ToString(), From = from, To = to });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (from.HasValue || to.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(from, to);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error("Range download HTTP error", eventCode: "DOWNLOAD_RANGE_HTTP_ERROR", context: new
                {
                    Url = url.ToString(),
                    StatusCode = (int)response.StatusCode,
                    Reason = response.ReasonPhrase
                });
                var statusCode = (int)response.StatusCode;
                var reason = response.ReasonPhrase ?? "Unknown";
                throw new HttpRequestException($"Failed to download range. Status code: {statusCode} ({reason}).");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int bytesRead;
            while ((bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                progress?.Report(bytesRead);
            }

            _logger.Info("Completed range download", eventCode: "DOWNLOAD_RANGE_SUCCESS", context: new { Url = url.ToString(), From = from, To = to });
        }
        catch (HttpRequestException ex)
        {
            _logger.Error("Range download failed with HTTP error", eventCode: "DOWNLOAD_RANGE_HTTP_EXCEPTION", exception: ex, context: new { Url = url.ToString(), From = from, To = to });
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.Error("Range download was canceled", eventCode: "DOWNLOAD_RANGE_CANCELED", exception: ex, context: new { Url = url.ToString(), From = from, To = to });
            throw;
        }
    }

    private static void ExtractMetadata(
        HttpResponseMessage response,
        out long? contentLength,
        out string? etag,
        out DateTimeOffset? lastModified,
        out bool isChunkedWithoutLength)
    {
        contentLength = response.Content.Headers.ContentLength;
        etag = response.Headers.ETag?.ToString();
        lastModified = response.Content.Headers.LastModified;

        isChunkedWithoutLength = response.Headers.TransferEncodingChunked == true && contentLength is null;
    }

    private static async Task<bool> CheckRangeSupportAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return false;
    }
}
