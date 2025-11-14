using System;
using System.Collections.Generic;
using System.Linq;
using SharpDownloadManager.Core.Utilities;

namespace SharpDownloadManager.UI.Services;

internal static class BrowserDownloadSafetyInspector
{
    private static readonly string[] SuspiciousHostKeywords =
    {
        "ads",
        "adclick",
        "redirect",
        "trk",
        "track",
        "analytics"
    };

    public static BrowserDownloadPromptMessage? Analyze(Uri? uri, string? suggestedFileName)
    {
        if (uri is null)
        {
            if (FileNameHelper.LooksLikePlaceholderName(suggestedFileName))
            {
                return new BrowserDownloadPromptMessage(
                    "The current file name looks temporary. IDMFree will update it once the server reveals the actual name.",
                    isWarning: false);
            }

            return null;
        }

        var messages = new List<string>();
        var isWarning = false;

        var placeholder = FileNameHelper.LooksLikePlaceholderName(suggestedFileName);
        if (placeholder)
        {
            messages.Add("The current file name looks temporary. IDMFree will update it once the server reveals the actual name.");
        }

        var queryLength = uri.Query?.Length ?? 0;
        if (queryLength > 1200)
        {
            messages.Add("The link is extremely long which is typical for redirect or ad tracking URLs.");
            isWarning = true;
        }
        else if (queryLength > 512 && placeholder)
        {
            messages.Add("The link is very long and the provided file name looks temporary. It may be an intermediate redirect.");
            isWarning = true;
        }

        if (IsSuspiciousHost(uri.Host) && placeholder)
        {
            messages.Add("The host name resembles a redirect or tracking service. Double-check that this is the file you expect.");
            isWarning = true;
        }

        if (messages.Count == 0)
        {
            return null;
        }

        var distinctMessage = string.Join(Environment.NewLine, messages.Distinct());
        return new BrowserDownloadPromptMessage(distinctMessage, isWarning);
    }

    private static bool IsSuspiciousHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var lower = host.ToLowerInvariant();
        if (SuspiciousHostKeywords.Any(keyword => lower.Contains(keyword)))
        {
            return true;
        }

        var parts = lower.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            foreach (var part in parts)
            {
                if (part.Length > 24 && part.Any(char.IsDigit))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public sealed record BrowserDownloadPromptMessage(string Message, bool IsWarning);
