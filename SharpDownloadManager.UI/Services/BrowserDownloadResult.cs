using System.Net;
using SharpDownloadManager.Core.Domain;

namespace SharpDownloadManager.UI.Services;

public sealed record BrowserDownloadResult(
    bool Success,
    HttpStatusCode StatusCode,
    DownloadTask? Task,
    string? Error,
    bool UserDeclined,
    string Status)
{
    public bool Handled => string.Equals(Status, "accepted", StringComparison.OrdinalIgnoreCase);

    public static BrowserDownloadResult Accepted(DownloadTask task) =>
        new(true, HttpStatusCode.Accepted, task, null, false, "accepted");

    public static BrowserDownloadResult Declined(string? message) =>
        new(false, HttpStatusCode.OK, null, message, true, "fallback");

    public static BrowserDownloadResult Fallback(HttpStatusCode statusCode, string? message) =>
        new(false, statusCode, null, message, false, "fallback");

    public static BrowserDownloadResult Failed(HttpStatusCode statusCode, string? message) =>
        new(false, statusCode, null, message, false, "error");
}
