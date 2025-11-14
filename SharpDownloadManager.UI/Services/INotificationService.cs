using System;

namespace SharpDownloadManager.UI.Services;

public interface INotificationService : IDisposable
{
    void ShowDownloadCompleted(string fileName, string? targetFolder);
}
