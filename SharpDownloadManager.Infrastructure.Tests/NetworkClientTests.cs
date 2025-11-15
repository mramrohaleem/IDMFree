using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
}
