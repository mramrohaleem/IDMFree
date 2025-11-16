using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Sockets;
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

    [Fact]
    public async Task DownloadRangeToStreamAsync_PostRequest_ForwardsBodyAndContentType()
    {
        var logger = new TestLogger();
        var downloadUri = new Uri("https://post-download.test/file.bin");
        var payload = Encoding.UTF8.GetBytes("binary-response");

        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return response;
        });

        var client = new NetworkClient(logger, handler);
        await using var destination = new MemoryStream();

        var metadata = await client.DownloadRangeToStreamAsync(
            downloadUri,
            null,
            null,
            destination,
            cancellationToken: default,
            extraHeaders: null,
            requestMethod: HttpMethod.Post,
            requestBody: Encoding.UTF8.GetBytes("token=abc123"),
            requestBodyContentType: "application/x-www-form-urlencoded").ConfigureAwait(false);

        Assert.Equal(payload.Length, destination.Length);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        var loggedRequest = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post.Method, loggedRequest.Method);
        Assert.Equal("application/x-www-form-urlencoded", loggedRequest.ContentType);
        Assert.Equal("token=abc123", loggedRequest.Body);
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_PostRejected_FallsBackToGet()
    {
        var logger = new TestLogger();
        var downloadUri = new Uri("https://post-download.test/file.bin");
        var payload = Encoding.UTF8.GetBytes("retry-payload");
        var attempt = 0;

        var handler = new RecordingHandler(request =>
        {
            attempt++;
            if (attempt == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return response;
        });

        var client = new NetworkClient(logger, handler);
        await using var destination = new MemoryStream();

        var metadata = await client.DownloadRangeToStreamAsync(
            downloadUri,
            null,
            null,
            destination,
            cancellationToken: default,
            extraHeaders: null,
            requestMethod: HttpMethod.Post,
            requestBody: Encoding.UTF8.GetBytes("token=retry"),
            requestBodyContentType: "application/x-www-form-urlencoded").ConfigureAwait(false);

        Assert.Equal(payload.Length, destination.Length);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Post.Method, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Get.Method, handler.Requests[1].Method);
        Assert.Equal("token=retry", handler.Requests[0].Body);
        Assert.Null(handler.Requests[1].Body);
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_GofileHost_MintsGuestTokenAndCachesAcrossClients()
    {
        await using var server = await GofileLikeTestServer.StartAsync().ConfigureAwait(false);
        server.RespondWithHtmlOnInvalidToken = true;
        server.ClearAllowedTokens();

        var apiState = new GofileApiStubState(new[] { true });
        var downloadUri = new Uri($"http://store-eu-par-1.gofile.io:{server.Port}/file");

        await using var firstDestination = new MemoryStream();
        var (firstClient, firstApiHandler, firstTransport) = CreateGofileClient(server, apiState);
        var firstMetadata = await firstClient.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                firstDestination)
            .ConfigureAwait(false);

        Assert.Equal("application/octet-stream", firstMetadata.ContentType);
        Assert.True(firstDestination.Length > 0);
        Assert.Equal(1, apiState.RequestCount);

        firstApiHandler.Dispose();
        firstTransport.Dispose();

        await using var secondDestination = new MemoryStream();
        var (secondClient, secondApiHandler, secondTransport) = CreateGofileClient(server, apiState);
        var secondMetadata = await secondClient.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                secondDestination)
            .ConfigureAwait(false);

        Assert.Equal("application/octet-stream", secondMetadata.ContentType);
        Assert.True(secondDestination.Length > 0);
        Assert.Equal(1, apiState.RequestCount);

        secondApiHandler.Dispose();
        secondTransport.Dispose();
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_GofileHost_RetriesHtmlResponseWithFreshToken()
    {
        await using var server = await GofileLikeTestServer.StartAsync().ConfigureAwait(false);
        server.RespondWithHtmlOnInvalidToken = true;
        server.ClearAllowedTokens();

        var apiState = new GofileApiStubState(new[] { false, true });
        var downloadUri = new Uri($"http://store-eu-par-1.gofile.io:{server.Port}/file");

        await using var destination = new MemoryStream();
        var (client, apiHandler, transport) = CreateGofileClient(server, apiState);
        var metadata = await client.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                destination)
            .ConfigureAwait(false);

        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.True(destination.Length > 0);
        Assert.Equal(2, apiState.RequestCount);

        apiHandler.Dispose();
        transport.Dispose();
    }

    [Theory]
    [InlineData("https://gofile.io/d/resolverId")]
    [InlineData("https://gofile.io/d/123e4567-e89b-12d3-a456-426614174000")]
    [InlineData("https://gofile.io/d/identifier_with_underscores")]
    public async Task DownloadRangeToStreamAsync_GofileResolver_UsesContentsApiLink(string refererUrl)
    {
        var logger = new TestLogger();
        var referer = new Uri(refererUrl);
        var downloadUri = new Uri("https://store-eu-par-1.gofile.io/download/original");
        var resolvedUri = new Uri("https://cdn.gofile.io/download/resolved.bin");
        var payload = Encoding.UTF8.GetBytes("resolved-binary");
        var servedHtml = false;
        var tokenRequestCount = 0;

        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri is null)
            {
                throw new InvalidOperationException("Request URI must not be null.");
            }

            if (request.RequestUri.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequestUri.AbsolutePath.Contains("/accounts", StringComparison.OrdinalIgnoreCase))
                {
                    tokenRequestCount++;
                    var tokenPayload = $"{{\"data\":{{\"token\":\"guest-token-{tokenRequestCount}\"}}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tokenPayload, Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath.Contains("/contents/", StringComparison.OrdinalIgnoreCase))
                {
                    var payloadJson = $"{{\"data\":{{\"link\":\"{resolvedUri}\"}}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                    };
                }
            }

            if (request.RequestUri == downloadUri)
            {
                if (!servedHtml)
                {
                    servedHtml = true;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>Link expired</body></html>", Encoding.UTF8, "text/html")
                    };
                }
            }

            if (request.RequestUri == resolvedUri)
            {
                var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                okResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return okResponse;
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        await using var destination = new MemoryStream();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Referer"] = referer.ToString()
        };
        var client = new NetworkClient(logger, handler);

        var metadata = await client.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                destination,
                cancellationToken: default,
                extraHeaders: headers)
            .ConfigureAwait(false);

        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Equal(payload.Length, destination.Length);
        Assert.Contains(handler.Requests, request =>
            request.Uri.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase) &&
            request.Uri.AbsolutePath.Contains("/contents/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, request => request.Uri == resolvedUri);
    }

    [Theory]
    [InlineData("https://store-eu-par-1.gofile.io/download/web/resolverId/resolved.bin")]
    [InlineData("https://store-eu-par-2.gofile.io/download/direct/resolverId/resolved.bin")]
    public async Task DownloadRangeToStreamAsync_GofileResolver_DetectsContentIdFromDownloadPath(string downloadUrl)
    {
        var logger = new TestLogger();
        var downloadUri = new Uri(downloadUrl);
        var resolvedUri = new Uri("https://cdn.gofile.io/download/resolved.bin");
        var payload = Encoding.UTF8.GetBytes("resolved-binary");
        var servedHtml = false;
        var tokenRequestCount = 0;

        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri is null)
            {
                throw new InvalidOperationException("Request URI must not be null.");
            }

            if (request.RequestUri.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequestUri.AbsolutePath.Contains("/accounts", StringComparison.OrdinalIgnoreCase))
                {
                    tokenRequestCount++;
                    var tokenPayload = $"{{\"data\":{{\"token\":\"guest-token-{tokenRequestCount}\"}}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(tokenPayload, Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath.Contains("/contents/", StringComparison.OrdinalIgnoreCase))
                {
                    var payloadJson = $"{{\"data\":{{\"link\":\"{resolvedUri}\"}}}}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
                    };
                }
            }

            if (request.RequestUri == downloadUri)
            {
                if (!servedHtml)
                {
                    servedHtml = true;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<html><body>Link expired</body></html>", Encoding.UTF8, "text/html")
                    };
                }
            }

            if (request.RequestUri == resolvedUri)
            {
                var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                okResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return okResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await using var destination = new MemoryStream();
        var client = new NetworkClient(logger, handler);

        var metadata = await client.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                destination)
            .ConfigureAwait(false);

        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Equal(payload.Length, destination.Length);
        Assert.Contains(handler.Requests, request =>
            request.Uri.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase) &&
            request.Uri.AbsolutePath.Contains("/contents/", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, request => request.Uri == resolvedUri);
    }

    [Fact]
    public async Task DownloadRangeToStreamAsync_CustomHtmlResolver_PipelineInvoked()
    {
        var logger = new TestLogger();
        var downloadUri = new Uri("https://html-resolver.test/file");
        var resolvedUri = new Uri("https://resolved.test/file.bin");
        var payload = Encoding.UTF8.GetBytes("custom-resolver");

        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri == downloadUri)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<html><body>no direct link</body></html>", Encoding.UTF8, "text/html")
                };
            }

            if (request.RequestUri == resolvedUri)
            {
                var okResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                };
                okResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return okResponse;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var resolver = new StubHtmlResolver(context =>
            context.HtmlSnippet.Contains("no direct link", StringComparison.OrdinalIgnoreCase)
                ? resolvedUri
                : null);

        await using var destination = new MemoryStream();
        var client = new NetworkClient(logger, handler, new[] { resolver });

        var metadata = await client.DownloadRangeToStreamAsync(
                downloadUri,
                null,
                null,
                destination)
            .ConfigureAwait(false);

        Assert.True(resolver.Invoked);
        Assert.Equal("application/octet-stream", metadata.ContentType);
        Assert.Equal(payload.Length, destination.Length);
        Assert.Contains(handler.Requests, request => request.Uri == resolvedUri);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        }

        public List<LoggedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await LoggedRequest.FromAsync(request, cancellationToken).ConfigureAwait(false));
            var response = _responder(request);
            return response;
        }
    }

    private sealed record LoggedRequest(Uri Uri, string? Referer, string Method, string? Body, string? ContentType)
    {
        public static async Task<LoggedRequest> FromAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new LoggedRequest(
                request.RequestUri!,
                request.Headers.Referrer?.ToString(),
                request.Method.Method,
                body,
                request.Content?.Headers.ContentType?.ToString());
        }
    }

    private sealed class StubHtmlResolver : IHtmlDownloadResolver
    {
        private readonly Func<HtmlDownloadResolverContext, Uri?> _factory;

        public StubHtmlResolver(Func<HtmlDownloadResolverContext, Uri?> factory)
        {
            _factory = factory;
        }

        public bool Invoked { get; private set; }

        public Task<Uri?> TryResolveAsync(HtmlDownloadResolverContext context, CancellationToken cancellationToken)
        {
            Invoked = true;
            return Task.FromResult(_factory(context));
        }
    }

    private static (NetworkClient Client, FakeGofileApiHandler ApiHandler, SocketsHttpHandler TransportHandler) CreateGofileClient(
        GofileLikeTestServer server,
        GofileApiStubState apiState)
    {
        var transport = CreateLoopbackHandler(server);
        var apiHandler = new FakeGofileApiHandler(server, apiState)
        {
            InnerHandler = transport
        };
        var logger = new TestLogger();
        var client = new NetworkClient(logger, apiHandler);
        return (client, apiHandler, transport);
    }

    private static SocketsHttpHandler CreateLoopbackHandler(GofileLikeTestServer server)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                var endPoint = new IPEndPoint(IPAddress.Loopback, server.Port);
                await socket.ConnectAsync(endPoint, token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
    }

    private sealed class GofileApiStubState
    {
        private readonly Queue<bool> _acceptancePlan;

        public GofileApiStubState(IEnumerable<bool>? acceptancePlan)
        {
            _acceptancePlan = acceptancePlan is null
                ? new Queue<bool>()
                : new Queue<bool>(acceptancePlan);
        }

        public int RequestCount { get; set; }

        public bool ShouldAcceptNextToken()
        {
            if (_acceptancePlan.Count == 0)
            {
                return true;
            }

            return _acceptancePlan.Dequeue();
        }
    }

    private sealed class FakeGofileApiHandler : DelegatingHandler
    {
        private readonly GofileLikeTestServer _server;
        private readonly GofileApiStubState _state;

        public FakeGofileApiHandler(GofileLikeTestServer server, GofileApiStubState state)
        {
            _server = server;
            _state = state;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host.Equals("api.gofile.io", StringComparison.OrdinalIgnoreCase) == true)
            {
                _state.RequestCount++;
                var token = $"guest-token-{_state.RequestCount}";
                if (_state.ShouldAcceptNextToken())
                {
                    _server.AllowToken(token);
                }

                var payload = $"{{\"status\":\"ok\",\"data\":{{\"token\":\"{token}\"}}}}";
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };

                return Task.FromResult(response);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
