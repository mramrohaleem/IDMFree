using System.Net;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.UI.Services;

public sealed record BrowserDownloadResult(
    bool Success,
    bool Handled,
    HttpStatusCode StatusCode,
    DownloadTask? Task,
    string? Error,
    bool UserDeclined)
{
    public static BrowserDownloadResult Accepted(DownloadTask task) =>
        new(true, true, HttpStatusCode.Accepted, task, null, false);

    public static BrowserDownloadResult Declined(string? message) =>
        new(false, true, HttpStatusCode.Conflict, null, message, true);

    public static BrowserDownloadResult Failed(
        HttpStatusCode statusCode,
        string? message,
        bool handled = false) =>
        new(false, handled, statusCode, null, message, false);
}
