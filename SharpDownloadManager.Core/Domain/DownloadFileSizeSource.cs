namespace SharpDownloadManager.Core.Domain;

public static class DownloadFileSizeSource
{
    public const string ContentLength = "content-length";
    public const string ContentRange = "content-range";
    public const string HttpHeaders = "http-headers";
    public const string Approximation = "approximation";
    public const string DiskFinal = "disk-final";
    public const string Unknown = "unknown";
}
