using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Infrastructure.Network;
using SharpDownloadManager.Infrastructure.Tests.TestHelpers;

namespace SharpDownloadManager.Infrastructure.Tests;

public sealed class NetworkClientTests
{
    [Fact]
    public async Task DownloadRangeToStreamAsync_FollowsRedirectsAndKeepsCookies()
    {
        await using var server = await GofileLikeTestServer.StartAsync().ConfigureAwait(false);
        var logger = new TestLogger();
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = true
        };

        var client = new NetworkClient(logger, handler);
        await using var destination = new MemoryStream();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "accountToken=magic",
            ["Referer"] = server.StartUri.ToString()
        };

        var metadata = await client.DownloadRangeToStreamAsync(
            server.StartUri,
            null,
            null,
            destination,
            cancellationToken: default,
            extraHeaders: headers).ConfigureAwait(false);

        Assert.True(destination.Length > 0);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Contains(server.GetRequestsSnapshot(), request =>
            request.Path.Equals("/file", StringComparison.OrdinalIgnoreCase) &&
            request.Headers.TryGetValue("Cookie", out var forwardedCookie) &&
            forwardedCookie.Contains("accountToken=magic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_ThrowsFriendlyErrorOn404()
    {
        await using var server = await GofileLikeTestServer.StartAsync().ConfigureAwait(false);
        var logger = new TestLogger();
        var client = new NetworkClient(logger);
        await using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadRangeToStreamAsync(
            server.MissingUri,
            null,
            null,
            destination)).ConfigureAwait(false);

        Assert.Contains("404", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_GofileSubdomain_KeepsProvidedReferer()
    {
        var logger = new TestLogger();
        const string referer = "https://gofile.io/d/share-token";
        var downloadUri = new Uri("https://store-eu-par-1.gofile.io/download/file");
        var payload = Encoding.UTF8.GetBytes("gofile-test");

        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(downloadUri, request.RequestUri);
            Assert.Equal(referer, request.Headers.Referrer?.ToString());

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return response;
        });

        var client = new NetworkClient(logger, handler);
        await using var destination = new MemoryStream();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = referer
        };

        var metadata = await client.DownloadRangeToStreamAsync(
            downloadUri,
            null,
            null,
            destination,
            cancellationToken: default,
            extraHeaders: headers).ConfigureAwait(false);

        Assert.Equal(payload.Length, destination.Length);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_GofileSubdomain_DoesNotTriggerHtmlFallbackWhenRefererProvided()
    {
        var logger = new TestLogger();
        const string referer = "https://gofile.io/d/share-token";
        var downloadUri = new Uri("https://store-eu-par-1.gofile.io/download/file");
        var payload = Encoding.UTF8.GetBytes("binary");

        var handler = new RecordingHandler(request =>
        {
            if (request.Headers.Referrer?.ToString() == referer &&
                request.RequestUri?.Host.Contains("gofile.io", StringComparison.OrdinalIgnoreCase) == true)
            {
                var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                okResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return okResponse;
            }

            var htmlResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<html><body>Download via <a href=\"https://fallback.test/file.zip\">Download</a></body></html>",
                    Encoding.UTF8,
                    "text/html")
            };
            return htmlResponse;
        });

        var client = new NetworkClient(logger, handler);
        await using var destination = new MemoryStream();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = referer
        };

        var metadata = await client.DownloadRangeToStreamAsync(
            downloadUri,
            null,
            null,
            destination,
            cancellationToken: default,
            extraHeaders: headers).ConfigureAwait(false);

        Assert.Equal(payload.Length, destination.Length);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Single(handler.Requests, request => request.Uri.Host.Contains("gofile.io", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host.Equals("fallback.test", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        public List<LoggedRequest> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(LoggedRequest.From(request));
            var response = _responder(request);
            return Task.FromResult(response);
        }
    }

    private sealed record LoggedRequest(Uri Uri, string? Referer)
    {
        public static LoggedRequest From(HttpRequestMessage request)
        {
            return new LoggedRequest(request.RequestUri!, request.Headers.Referrer?.ToString());
        }
    }
}
