using System.Net;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.UI.Services;

public sealed record BrowserDownloadResult(
    bool Success,
    HttpStatusCode StatusCode,
    DownloadTask? Task,
    string? Error,
    bool UserDeclined)
{
    public static BrowserDownloadResult Accepted(DownloadTask task) =>
        new(true, HttpStatusCode.Accepted, task, null, false);

    public static BrowserDownloadResult Declined(string? message) =>
        new(false, HttpStatusCode.Conflict, null, message, true);

    public static BrowserDownloadResult Failed(HttpStatusCode statusCode, string? message) =>
        new(false, statusCode, null, message, false);
}
