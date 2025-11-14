using System;
using System.Drawing;
using SharpDownloadManager.Core.Abstractions;
using WinForms = System.Windows.Forms;

namespace SharpDownloadManager.UI.Services;

public sealed class NotificationService : INotificationService
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly ILogger _logger;
    private bool _disposed;

    public NotificationService(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Visible = true,
            Icon = SystemIcons.Application,
            Text = "IDMFree"
        };
    }

    public void ShowDownloadCompleted(string fileName, string? targetFolder)
    {
        if (_disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "Download";
        }

        var message = string.IsNullOrWhiteSpace(targetFolder)
            ? fileName
            : $"{fileName}\n{targetFolder}";

        _notifyIcon.BalloonTipTitle = "Download completed";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(4000);

        _logger.Info(
            "Completion notification shown.",
            eventCode: "DOWNLOAD_COMPLETED_NOTIFICATION_SHOWN",
            context: new { FileName = fileName, TargetFolder = targetFolder });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
