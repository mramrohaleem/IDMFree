using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Infrastructure.Logging;
using SharpDownloadManager.Infrastructure.Network.Resolvers;

namespace SharpDownloadManager.Infrastructure.Tests;

public sealed class GofileHtmlResolverTests
{
    [Fact]
    public async Task TryResolveAsync_AttachesAuthorizationAndParsesLink()
    {
        var handler = new TestHandler();
        using var httpClient = new HttpClient(handler);
        var resolver = new GofileHtmlResolver(
            httpClient,
            new TestLogger(),
            (_, _) => Task.FromResult<string?>("account-token"));

        var context = new HtmlDownloadResolverContext(
            new Uri("https://store-eu-par-3.gofile.io/download/web/96da89db-eebf-47fb-bd19-a1c5731e3360/ProjectZomboid.v42.12.3.RexaGames.com.rar"),
            new Uri("https://gofile.io/d/bf3bb553-122f-489a-a4b2-e714d50e011f"),
            "<html></html>",
            new Dictionary<string, string>(),
            new Uri("https://gofile.io/d/bf3bb553-122f-489a-a4b2-e714d50e011f"));

        var result = await resolver.TryResolveAsync(context, CancellationToken.None).ConfigureAwait(false);

        Assert.Equal("https://files.test/download.bin", result?.ToString());
        Assert.Contains(handler.Requests, request =>
            request.RequestUri == TestHandler.WebsiteScriptUri);
        Assert.Contains(handler.Requests, request =>
            request.RequestUri?.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase) == true &&
            request.Authorization?.Parameter == "account-token" &&
            (request.RequestUri?.Query?.Contains("wt=site-token", StringComparison.Ordinal) ?? false));
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        internal static readonly Uri WebsiteScriptUri = new("https://gofile.io/dist/js/global.js");
        private readonly List<RequestSnapshot> _requests = new();

        public IReadOnlyList<RequestSnapshot> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(new RequestSnapshot(request.RequestUri, request.Headers.Authorization));

            if (request.RequestUri == WebsiteScriptUri)
            {
                var script = "const noop = 0;\nappdata.wt = \"site-token\";";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(script, Encoding.UTF8, "application/javascript")
                });
            }

            if (request.RequestUri?.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase) == true)
            {
                var json = "{\"status\":\"ok\",\"data\":{\"link\":\"https://files.test/download.bin\"}}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        internal sealed record RequestSnapshot(Uri? RequestUri, System.Net.Http.Headers.AuthenticationHeaderValue? Authorization);
    }
}
