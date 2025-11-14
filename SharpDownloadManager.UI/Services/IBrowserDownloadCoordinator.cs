using System.Threading;
using System.Threading.Tasks;
using SharpDownloadManager.Core.Abstractions;

namespace SharpDownloadManager.UI.Services;

public interface IBrowserDownloadCoordinator
{
    Task<BrowserDownloadResult> HandleAsync(BrowserDownloadRequest request, CancellationToken cancellationToken);
}
