using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SharpDownloadManager.Core.Abstractions;

/// <summary>
/// Represents a component that can translate unexpected HTML responses into a file download URL.
/// </summary>
public interface IHtmlDownloadResolver
{
    /// <summary>
    /// Attempts to resolve the actual download URL for the provided HTML context.
    /// </summary>
    /// <param name="context">Information about the failed download attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resolved download URL or <c>null</c> when the resolver cannot handle the response.</returns>
    Task<Uri?> TryResolveAsync(HtmlDownloadResolverContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Provides the context needed by <see cref="IHtmlDownloadResolver"/> implementations.
/// </summary>
public sealed record HtmlDownloadResolverContext(
    Uri OriginalUrl,
    Uri? ResponseUrl,
    string HtmlSnippet,
    IReadOnlyDictionary<string, string>? ExtraHeaders,
    Uri? Referer);
