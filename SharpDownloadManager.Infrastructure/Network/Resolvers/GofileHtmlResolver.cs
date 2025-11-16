using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Infrastructure.Logging;

namespace SharpDownloadManager.Infrastructure.Network.Resolvers;

internal sealed class GofileHtmlResolver : IHtmlDownloadResolver
{
    private static readonly Uri GofileContentsRoot = new("https://api.gofile.io/contents/");
    private static readonly string[][] DownloadContentSegmentMarkers =
    {
        new[] { "download", "web" },
        new[] { "download", "direct" },
        new[] { "download", "secure" },
        new[] { "download", "token" }
    };

    private readonly HttpClient _client;
    private readonly ILogger _logger;
    private readonly Func<Uri, CancellationToken, Task<string?>> _tokenAccessor;

    public GofileHtmlResolver(
        HttpClient client,
        ILogger logger,
        Func<Uri, CancellationToken, Task<string?>> tokenAccessor)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
    }

    public async Task<Uri?> TryResolveAsync(HtmlDownloadResolverContext context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (!IsGofileContext(context))
        {
            return null;
        }

        var contentId = TryExtractContentId(context.ResponseUrl) ??
                        TryExtractContentId(context.Referer) ??
                        TryExtractContentId(context.OriginalUrl);

        if (string.IsNullOrWhiteSpace(contentId))
        {
            return null;
        }

        var tokenSource = context.ResponseUrl ?? context.OriginalUrl;
        if (tokenSource is null)
        {
            return null;
        }

        var token = await _tokenAccessor(tokenSource, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var endpoint = new Uri(GofileContentsRoot, contentId + "?wt=" + Uri.EscapeDataString(token));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        if (context.Referer is not null)
        {
            request.Headers.Referrer = context.Referer;
        }

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return TryExtractLinkFromPayload(payload);
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(
                "Gofile resolver failed to contact contents API.",
                eventCode: "GOFILE_HTML_RESOLVER_HTTP",
                exception: ex,
                context: new { Url = context.OriginalUrl.ToString() });
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                "Gofile resolver encountered an unexpected error.",
                eventCode: "GOFILE_HTML_RESOLVER_UNKNOWN",
                exception: ex,
                context: new { Url = context.OriginalUrl.ToString() });
        }

        return null;
    }

    private static bool IsGofileContext(HtmlDownloadResolverContext context)
    {
        return IsGofileHost(context.ResponseUrl) ||
               IsGofileHost(context.Referer) ||
               IsGofileHost(context.OriginalUrl);
    }

    private static bool IsGofileHost(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        return uri.Host.EndsWith("gofile.io", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractContentId(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var contentIdFromDownloadPath = TryExtractContentIdFromDownloadPath(segments);
        if (!string.IsNullOrWhiteSpace(contentIdFromDownloadPath))
        {
            return contentIdFromDownloadPath;
        }

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = segments[i + 1];
                if (IsLikelyContentId(candidate))
                {
                    return candidate;
                }
            }
        }

        if (segments.Length > 0)
        {
            var last = segments[^1];
            if (IsLikelyContentId(last))
            {
                return last;
            }
        }

        return null;
    }

    private static string? TryExtractContentIdFromDownloadPath(string[] segments)
    {
        if (segments.Length < 3)
        {
            return null;
        }

        foreach (var markers in DownloadContentSegmentMarkers)
        {
            var candidate = TryExtractContentIdFromMarkers(segments, markers);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryExtractContentIdFromMarkers(string[] segments, string[] markers)
    {
        if (markers.Length == 0)
        {
            return null;
        }

        for (var i = 0; i <= segments.Length - markers.Length - 1; i++)
        {
            var isMatch = true;
            for (var j = 0; j < markers.Length; j++)
            {
                if (!segments[i + j].Equals(markers[j], StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = false;
                    break;
                }
            }

            if (!isMatch)
            {
                continue;
            }

            var candidateIndex = i + markers.Length;
            var candidate = segments[candidateIndex];
            if (IsLikelyContentId(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsLikelyContentId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (candidate.Length is < 4 or > 40)
        {
            return false;
        }

        return candidate.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    private Uri? TryExtractLinkFromPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("data", out var dataElement) &&
                dataElement.TryGetProperty("link", out var linkElement))
            {
                var link = linkElement.GetString();
                if (!string.IsNullOrWhiteSpace(link) && Uri.TryCreate(link, UriKind.Absolute, out var uri))
                {
                    return uri;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.Warn(
                "Gofile resolver failed to parse contents response.",
                eventCode: "GOFILE_HTML_RESOLVER_PARSE",
                exception: ex,
                context: new { PayloadSnippet = payload.Length > 256 ? payload[..256] : payload });
        }

        return null;
    }
}
