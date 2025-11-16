using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SharpDownloadManager.Infrastructure.Tests.TestHelpers;

internal sealed class GofileLikeTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentBag<HttpRequestSnapshot> _requests = new();
    private readonly HashSet<string> _allowedTokens = new(StringComparer.Ordinal);
    private readonly object _tokenLock = new();
    private readonly byte[] _payload;

    private GofileLikeTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{Port}/");
        _payload = Encoding.UTF8.GetBytes("test-payload-from-server");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        AllowToken("magic");
    }

    public int Port { get; }

    public Uri BaseUri { get; }

    public Uri StartUri => new(BaseUri, "start");

    public Uri MissingUri => new(BaseUri, "missing");

    public bool RespondWithHtmlOnInvalidToken { get; set; }

    public IReadOnlyCollection<HttpRequestSnapshot> GetRequestsSnapshot()
    {
        return _requests.ToArray();
    }

    public static Task<GofileLikeTestServer> StartAsync()
    {
        var server = new GofileLikeTestServer();
        return Task.FromResult(server);
    }

    public void AllowToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (_tokenLock)
        {
            _allowedTokens.Add(token);
        }
    }

    public void ClearAllowedTokens()
    {
        lock (_tokenLock)
        {
            _allowedTokens.Clear();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        await using var network = client;
        await using var stream = network.GetStream();
        var request = await HttpRequestSnapshot.ReadAsync(stream, token).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        _requests.Add(request);

        if (request.Path.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(stream, HttpStatusCode.Found, body: null, extraHeaders: new Dictionary<string, string>
            {
                ["Location"] = new Uri(BaseUri, "file").ToString()
            }, request.Method, token).ConfigureAwait(false);
            return;
        }

        if (request.Path.Equals("/file", StringComparison.OrdinalIgnoreCase))
        {
            var tokenValue = request.Headers.TryGetValue("Cookie", out var cookie)
                ? ExtractAccountToken(cookie)
                : null;

            if (!IsTokenAllowed(tokenValue))
            {
                if (RespondWithHtmlOnInvalidToken)
                {
                    await WriteResponseAsync(
                            stream,
                            HttpStatusCode.OK,
                            Encoding.UTF8.GetBytes("<html><body>Login required</body></html>"),
                            method: request.Method,
                            extraHeaders: new Dictionary<string, string>
                            {
                                ["Content-Type"] = "text/html"
                            },
                            token: token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteResponseAsync(
                            stream,
                            HttpStatusCode.Forbidden,
                            Encoding.UTF8.GetBytes("Forbidden"),
                            method: request.Method,
                            token: token)
                        .ConfigureAwait(false);
                }

                return;
            }

            await WriteResponseAsync(
                    stream,
                    HttpStatusCode.OK,
                    _payload,
                    method: request.Method,
                    extraHeaders: new Dictionary<string, string>
                    {
                        ["Content-Type"] = "application/octet-stream"
                    },
                    token: token)
                .ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(stream, HttpStatusCode.NotFound, Encoding.UTF8.GetBytes("missing"), method: request.Method, token: token)
            .ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        byte[]? body,
        string method = "GET",
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        CancellationToken token = default)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append((int)statusCode)
            .Append(' ')
            .Append(statusCode)
            .Append("\r\n");

        var payloadLength = body?.Length ?? 0;
        builder.Append("Content-Length: ")
            .Append(payloadLength)
            .Append("\r\n");
        builder.Append("Connection: close\r\n");

        if (extraHeaders is not null)
        {
            foreach (var header in extraHeaders)
            {
                builder.Append(header.Key)
                    .Append(": ")
                    .Append(header.Value)
                    .Append("\r\n");
            }
        }

        builder.Append("\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token).ConfigureAwait(false);

        if (!string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase) && body is not null && body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private bool IsTokenAllowed(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (_tokenLock)
        {
            return _allowedTokens.Contains(token);
        }
    }

    private static string? ExtractAccountToken(string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        var segments = cookieHeader.Split(';');
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.StartsWith("accountToken=", StringComparison.Ordinal))
            {
                return trimmed.Substring("accountToken=".Length);
            }
        }

        return null;
    }

    internal sealed record HttpRequestSnapshot(string Method, string Path, IReadOnlyDictionary<string, string> Headers)
    {
        public static async Task<HttpRequestSnapshot?> ReadAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[4096];
            using var builder = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await builder.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

                if (builder.Length >= 4)
                {
                    var span = builder.GetBuffer().AsSpan(0, (int)builder.Length);
                    if (TryFindHeaderTerminator(span, out var headerLength))
                    {
                        var headerBytes = span[..headerLength].ToArray();
                        var headerText = Encoding.ASCII.GetString(headerBytes);
                        return ParseRequest(headerText);
                    }
                }
            }

            return null;
        }

        private static bool TryFindHeaderTerminator(ReadOnlySpan<byte> buffer, out int length)
        {
            for (var i = 3; i < buffer.Length; i++)
            {
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                {
                    length = i - 3;
                    return true;
                }
            }

            length = 0;
            return false;
        }

        private static HttpRequestSnapshot ParseRequest(string headerText)
        {
            using var reader = new StringReader(headerText);
            var requestLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                throw new InvalidOperationException("Invalid HTTP request received in test server.");
            }

            var parts = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var path = parts.Length > 1 ? parts[1] : "/";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrEmpty(line))
                {
                    break;
                }

                var separatorIndex = line.IndexOf(':');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var name = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim();
                headers[name] = value;
            }

            return new HttpRequestSnapshot(method, path, headers);
        }
    }
}
