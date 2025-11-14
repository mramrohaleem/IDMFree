using System;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpDownloadManager.Core.Utilities;

public static class FileNameHelper
{
    private static readonly string[] ReservedWindowsNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string? NormalizeFileName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().Trim('"').Trim('\'', '"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var candidate = trimmed;
        if (candidate.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            candidate.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            candidate = Path.GetFileName(candidate);
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (candidate.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^".crdownload".Length];
        }

        candidate = candidate.Trim();
        if (candidate.Length == 0)
        {
            return null;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(candidate.Length);
        foreach (var ch in candidate)
        {
            builder.Append(invalidChars.Contains(ch) ? '_' : ch);
        }

        candidate = builder.ToString().Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(candidate);
        if (!string.IsNullOrEmpty(nameWithoutExtension) &&
            ReservedWindowsNames.Any(r => string.Equals(r, candidate, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(r, nameWithoutExtension, StringComparison.OrdinalIgnoreCase)))
        {
            var extension = Path.GetExtension(candidate);
            candidate = string.IsNullOrEmpty(extension)
                ? candidate + "_"
                : nameWithoutExtension + "_" + extension;
        }

        return candidate;
    }
}
