using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;
using SharpDownloadManager.Infrastructure.Logging;

namespace SharpDownloadManager.Infrastructure.Network.Resolvers;

internal sealed class GofileResolver : IGofileResolver, IHtmlDownloadResolver
{
    private static readonly Uri GofileContentsRoot = new("https://api.gofile.io/contents/");
    private static readonly Uri GofileWebsiteScript = new("https://gofile.io/dist/js/global.js");
    private static readonly string[][] DownloadContentSegmentMarkers =
    {
        new[] { "download", "web" },
        new[] { "download", "direct" },
        new[] { "download", "secure" },
        new[] { "download", "token" }
    };
    private static readonly Regex WebsiteTokenRegex = new(
        "appdata\\.wt\\s*=\\s*[\"'](?<token>[A-Za-z0-9]+)[\"']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _client;
    private readonly ILogger _logger;
    private readonly Func<Uri, CancellationToken, Task<string?>> _tokenAccessor;
    private readonly SemaphoreSlim _websiteTokenSemaphore = new(1, 1);
    private string? _cachedWebsiteToken;

    public GofileResolver(
        HttpClient client,
        ILogger logger,
        Func<Uri, CancellationToken, Task<string?>> tokenAccessor)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenAccessor = tokenAccessor ?? throw new ArgumentNullException(nameof(tokenAccessor));
    }

    public async Task<Uri?> ResolveDirectDownloadUrlAsync(Uri initialUrl, CancellationToken cancellationToken)
    {
        if (initialUrl is null)
        {
            throw new ArgumentNullException(nameof(initialUrl));
        }

        _logger.Info(
            "Gofile resolver direct resolve starting.",
            eventCode: "GOFILE_DEBUG_RESOLVER_ENTRY",
            context: new { InitialUrl = initialUrl.ToString() });

        if (!IsGofileHost(initialUrl))
        {
            _logger.Info(
                "Gofile resolver skipping non-Gofile URL.",
                eventCode: "GOFILE_DEBUG_RESOLVER_NOT_GOFILE",
                context: new { Url = initialUrl.ToString() });
            return null;
        }

        var referer = BuildDefaultReferer(initialUrl);
        var candidates = new[] { initialUrl };

        _logger.Info(
            "Gofile resolver prepared candidates.",
            eventCode: "GOFILE_DEBUG_RESOLVER_CANDIDATES",
            context: new
            {
                Candidates = candidates.Select(c => new
                {
                    Url = c?.ToString(),
                    IsGofile = c is not null && IsGofileHost(c)
                }).ToArray(),
                Referer = referer?.ToString()
            });

        try
        {
            return await ResolveFromCandidatesAsync(
                    candidates,
                    referer,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(
                "Gofile resolver encountered HTTP error during direct resolve.",
                eventCode: "GOFILE_DEBUG_RESOLVER_HTTP_ERROR",
                exception: ex,
                context: new
                {
                    InitialUrl = initialUrl.ToString(),
                    ExceptionType = ex.GetType().FullName,
                    Message = ex.Message
                });
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                "Gofile resolver encountered unexpected error during direct resolve.",
                eventCode: "GOFILE_DEBUG_RESOLVER_UNEXPECTED",
                exception: ex,
                context: new
                {
                    InitialUrl = initialUrl.ToString(),
                    ExceptionType = ex.GetType().FullName,
                    Message = ex.Message
                });
            throw;
        }
    }

    public async Task<Uri?> TryResolveAsync(HtmlDownloadResolverContext context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var isGofileContext = IsGofileContext(context);

        _logger.Info(
            "Gofile resolver evaluating HTML context.",
            eventCode: "GOFILE_DEBUG_HTML_CONTEXT_ENTRY",
            context: new
            {
                OriginalUrl = context.OriginalUrl?.ToString(),
                ResponseUrl = context.ResponseUrl?.ToString(),
                Referer = context.Referer?.ToString(),
                SnippetLength = context.Snippet?.Length ?? 0,
                IsGofileContext = isGofileContext
            });

        if (!isGofileContext)
        {
            return null;
        }

        _logger.Info(
            "Gofile resolver attempting HTML context resolution.",
            eventCode: "GOFILE_DEBUG_HTML_CONTEXT_RESOLVE_START",
            context: new { OriginalUrl = context.OriginalUrl?.ToString() });

        return await ResolveFromCandidatesAsync(
                new[] { context.ResponseUrl, context.Referer, context.OriginalUrl },
                context.Referer,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Uri?> ResolveFromCandidatesAsync(
        IReadOnlyList<Uri?> candidates,
        Uri? referer,
        CancellationToken cancellationToken)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        _logger.Info(
            "Gofile resolver processing candidates.",
            eventCode: "GOFILE_DEBUG_RESOLVE_FROM_CANDIDATES_START",
            context: new
            {
                Candidates = candidates.Where(c => c is not null).Select(c => c!.ToString()).ToArray(),
                Referer = referer?.ToString()
            });

        var contentId = TryExtractContentId(candidates);
        _logger.Info(
            "Gofile resolver candidate content ID result.",
            eventCode: "GOFILE_DEBUG_RESOLVE_FROM_CANDIDATES_CONTENT_ID",
            context: new
            {
                ContentId = contentId,
                HasContentId = !string.IsNullOrWhiteSpace(contentId)
            });

        if (string.IsNullOrWhiteSpace(contentId))
        {
            return null;
        }

        var tokenSource = candidates.FirstOrDefault(uri => uri is not null);
        if (tokenSource is null)
        {
            return null;
        }

        _logger.Info(
            "Gofile resolver requesting account token.",
            eventCode: "GOFILE_DEBUG_ACCOUNT_TOKEN_REQUEST",
            context: new { TokenSource = tokenSource.ToString() });

        var accountToken = await _tokenAccessor(tokenSource, cancellationToken).ConfigureAwait(false);
        _logger.Info(
            "Gofile resolver account token result.",
            eventCode: "GOFILE_DEBUG_ACCOUNT_TOKEN_RESULT",
            context: new
            {
                HasToken = !string.IsNullOrWhiteSpace(accountToken),
                TokenLength = accountToken?.Length,
                TokenSuffix = accountToken is { Length: >= 4 } ? accountToken[^4..] : null
            });

        if (string.IsNullOrWhiteSpace(accountToken))
        {
            return null;
        }

        var websiteToken = await GetWebsiteTokenAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info(
            "Gofile resolver website token result.",
            eventCode: "GOFILE_DEBUG_WEBSITE_TOKEN_RESULT",
            context: new
            {
                HasWebsiteToken = !string.IsNullOrWhiteSpace(websiteToken),
                TokenLength = websiteToken?.Length
            });

        if (string.IsNullOrWhiteSpace(websiteToken))
        {
            return null;
        }

        var endpoint = new Uri(GofileContentsRoot, contentId + "?wt=" + Uri.EscapeDataString(websiteToken));
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountToken);
        request.Headers.Referrer = referer ?? BuildDefaultReferer(tokenSource);

        _logger.Info(
            "Gofile resolver issuing contents API request.",
            eventCode: "GOFILE_DEBUG_CONTENTS_API_REQUEST",
            context: new
            {
                Endpoint = endpoint.ToString(),
                ContentId = contentId,
                HasAccountToken = !string.IsNullOrWhiteSpace(accountToken),
                HasWebsiteToken = !string.IsNullOrWhiteSpace(websiteToken)
            });

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.Info(
                "Gofile resolver received contents API response.",
                eventCode: "GOFILE_DEBUG_CONTENTS_API_RESPONSE",
                context: new
                {
                    StatusCode = (int)response.StatusCode,
                    IsSuccess = response.IsSuccessStatusCode
                });

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.Info(
                "Gofile resolver read contents API payload.",
                eventCode: "GOFILE_DEBUG_CONTENTS_API_PAYLOAD",
                context: new
                {
                    PayloadPreview = payload.Length > 300 ? payload[..300] : payload,
                    PayloadLength = payload.Length
                });

            var link = TryExtractLinkFromPayload(payload);
            _logger.Info(
                "Gofile resolver payload link extraction result.",
                eventCode: "GOFILE_DEBUG_CONTENTS_API_LINK_RESULT",
                context: new
                {
                    HasLink = link is not null,
                    Link = link?.ToString()
                });

            return link;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(
                "Gofile resolver failed to contact contents API.",
                eventCode: "GOFILE_HTML_RESOLVER_HTTP",
                exception: ex,
                context: new { Url = tokenSource.ToString() });
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
                context: new { Url = tokenSource.ToString() });
        }

        return null;
    }

    private string? TryExtractContentId(IReadOnlyList<Uri?> candidates)
    {
        if (candidates is null)
        {
            return null;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var contentId = TryExtractContentId(candidate);
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                _logger.Info(
                    "Gofile resolver extracted content ID from candidates.",
                    eventCode: "GOFILE_DEBUG_CONTENT_ID_FROM_CANDIDATES",
                    context: new
                    {
                        CandidateIndex = i,
                        CandidateUrl = candidate?.ToString(),
                        ContentId = contentId
                    });
                return contentId;
            }
        }

        return null;
    }

    private Uri? BuildDefaultReferer(Uri? candidate)
    {
        if (candidate is null)
        {
            return null;
        }

        var contentId = TryExtractContentId(candidate);
        if (!string.IsNullOrWhiteSpace(contentId))
        {
            return new Uri($"https://gofile.io/d/{contentId}");
        }

        if (candidate.Host.EndsWith("gofile.io", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri($"https://{candidate.Host}/");
        }

        return new Uri("https://gofile.io/");
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

    private string? TryExtractContentId(Uri? uri)
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
            _logger.Info(
                "Gofile resolver extracted content ID from download path.",
                eventCode: "GOFILE_DEBUG_CONTENT_ID_FROM_URI",
                context: new
                {
                    Url = uri.ToString(),
                    ContentId = contentIdFromDownloadPath,
                    Segments = segments
                });
            return contentIdFromDownloadPath;
        }

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("d", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = segments[i + 1];
                if (IsLikelyContentId(candidate))
                {
                    _logger.Info(
                        "Gofile resolver extracted content ID from /d path.",
                        eventCode: "GOFILE_DEBUG_CONTENT_ID_FROM_URI",
                        context: new
                        {
                            Url = uri.ToString(),
                            ContentId = candidate,
                            Segments = segments
                        });
                    return candidate;
                }
            }
        }

        if (segments.Length > 0)
        {
            var last = segments[^1];
            if (IsLikelyContentId(last))
            {
                _logger.Info(
                    "Gofile resolver extracted content ID from trailing segment.",
                    eventCode: "GOFILE_DEBUG_CONTENT_ID_FROM_URI",
                    context: new
                    {
                        Url = uri.ToString(),
                        ContentId = last,
                        Segments = segments
                    });
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

    private async Task<string?> GetWebsiteTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedWebsiteToken))
        {
            return _cachedWebsiteToken;
        }

        await _websiteTokenSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedWebsiteToken))
            {
                return _cachedWebsiteToken;
            }

            _logger.Info(
                "Gofile resolver requesting website token script.",
                eventCode: "GOFILE_DEBUG_WEBSITE_TOKEN_REQUEST",
                context: new { Endpoint = GofileWebsiteScript.ToString() });

            using var response = await _client.GetAsync(GofileWebsiteScript, cancellationToken).ConfigureAwait(false);

            _logger.Info(
                "Gofile resolver received website token response.",
                eventCode: "GOFILE_DEBUG_WEBSITE_TOKEN_RESPONSE",
                context: new
                {
                    StatusCode = (int)response.StatusCode,
                    IsSuccess = response.IsSuccessStatusCode
                });
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn(
                    "Gofile resolver failed to fetch website token script.",
                    eventCode: "GOFILE_HTML_RESOLVER_WT_HTTP",
                    context: new { Status = (int)response.StatusCode });
                return null;
            }

            var script = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = TryExtractWebsiteToken(script);
            _logger.Info(
                "Gofile resolver parsed website token.",
                eventCode: "GOFILE_DEBUG_WEBSITE_TOKEN_PARSED",
                context: new
                {
                    HasToken = !string.IsNullOrWhiteSpace(token),
                    TokenLength = token?.Length
                });
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.Warn(
                    "Gofile resolver failed to parse website token script.",
                    eventCode: "GOFILE_HTML_RESOLVER_WT_PARSE");
                return null;
            }

            _cachedWebsiteToken = token;
            return token;
        }
        catch (HttpRequestException ex)
        {
            _logger.Warn(
                "Gofile resolver failed to download website token script.",
                eventCode: "GOFILE_HTML_RESOLVER_WT_HTTP",
                exception: ex);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn(
                "Gofile resolver encountered an unexpected error while fetching website token.",
                eventCode: "GOFILE_HTML_RESOLVER_WT_UNKNOWN",
                exception: ex);
        }
        finally
        {
            _websiteTokenSemaphore.Release();
        }

        return null;
    }

    private static string? TryExtractWebsiteToken(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var match = WebsiteTokenRegex.Match(script);
        return match.Success ? match.Groups["token"].Value : null;
    }
}
