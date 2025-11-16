using System;
using System.Threading;
using System.Threading.Tasks;

namespace SharpDownloadManager.Core.Abstractions;

/// <summary>
/// Provides utilities for resolving direct download links for Gofile resources.
/// </summary>
public interface IGofileResolver
{
    /// <summary>
    /// Attempts to resolve a direct download URL for the provided Gofile resource.
    /// </summary>
    /// <param name="initialUrl">The initial URL that points to a Gofile resource.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The direct download URL or <c>null</c> when it cannot be resolved.</returns>
    Task<Uri?> ResolveDirectDownloadUrlAsync(Uri initialUrl, CancellationToken cancellationToken);
}
