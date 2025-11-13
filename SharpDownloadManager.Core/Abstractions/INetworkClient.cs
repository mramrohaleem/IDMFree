using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SharpDownloadManager.Core.Abstractions;

public interface INetworkClient
{
    /// <summary>
    /// Probes the specified URL to discover basic resource metadata
    /// such as Content-Length, ETag, Last-Modified, and whether Range
    /// requests are truly supported.
    /// </summary>
    Task<HttpResourceInfo> ProbeAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a byte range [from..to] (inclusive) from the given URL into the destination stream.
    /// If from/to are null, downloads from the start or to the end as appropriate.
    /// Reports incremental bytes read via progress (if provided).
    /// Throws on HTTP errors (4xx/5xx) or network failures.
    /// </summary>
    Task DownloadRangeToStreamAsync(
        Uri url,
        long? from,
        long? to,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowHtmlFallback = true);
}
