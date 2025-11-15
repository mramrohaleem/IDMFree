using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static readonly HashSet<string> QueryFileNameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "filename",
        "file",
        "name",
        "download",
        "title",
        "attachment"
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

        if (candidate.Contains('%'))
        {
            try
            {
                candidate = Uri.UnescapeDataString(candidate);
            }
            catch
            {
            }
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

    public static bool LooksLikePlaceholderName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        var normalized = NormalizeFileName(fileName);
        if (string.IsNullOrEmpty(normalized))
        {
            return true;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrEmpty(nameWithoutExtension))
        {
            return true;
        }

        var extension = Path.GetExtension(normalized);
        if (extension.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            if (nameWithoutExtension.Equals("download", StringComparison.OrdinalIgnoreCase) ||
                nameWithoutExtension.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (nameWithoutExtension.Length >= 40 && nameWithoutExtension.Length <= 160)
        {
            var tokenChars = 0;
            foreach (var ch in nameWithoutExtension)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '+')
                {
                    tokenChars++;
                }
            }

            var ratio = (double)tokenChars / nameWithoutExtension.Length;
            if (ratio > 0.9)
            {
                return true;
            }
        }

        return false;
    }

    public static string? TryExtractFileNameFromUrl(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        // First try to extract from query parameters commonly used for file names.
        var query = uri.Query;
        if (!string.IsNullOrEmpty(query) && query.Length > 1)
        {
            var span = query.AsSpan(1);
            while (!span.IsEmpty)
            {
                var separatorIndex = span.IndexOf('&');
                var current = separatorIndex >= 0 ? span[..separatorIndex] : span;

                var equalsIndex = current.IndexOf('=');
                if (equalsIndex > 0)
                {
                    var key = Uri.UnescapeDataString(current[..equalsIndex].ToString());
                    if (QueryFileNameKeys.Contains(key))
                    {
                        var value = current[(equalsIndex + 1)..];
                        if (!value.IsEmpty)
                        {
                            var decoded = Uri.UnescapeDataString(value.ToString());
                            var normalized = NormalizeFileName(decoded);
                            if (!string.IsNullOrEmpty(normalized))
                            {
                                return normalized;
                            }
                        }
                    }
                }

                if (separatorIndex < 0)
                {
                    break;
                }

                span = span[(separatorIndex + 1)..];
            }
        }

        var fileName = Path.GetFileName(uri.AbsolutePath);
        if (!string.IsNullOrEmpty(fileName))
        {
            try
            {
                fileName = Uri.UnescapeDataString(fileName);
            }
            catch
            {
            }
        }

        return NormalizeFileName(fileName);
    }

    public static string? NormalizeContentDispositionFileName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (trimmed.Contains("''", StringComparison.Ordinal))
        {
            var parts = trimmed.Split('\'');
            if (parts.Length >= 3)
            {
                var encodingName = parts[0];
                var encodedValue = string.Join("'", parts.Skip(2));
                try
                {
                    var encoding = string.IsNullOrWhiteSpace(encodingName)
                        ? Encoding.UTF8
                        : Encoding.GetEncoding(encodingName);
                    var decodedBytes = DecodePercentEncodedToBytes(encodedValue);
                    var decoded = encoding.GetString(decodedBytes);
                    var normalized = NormalizeFileName(decoded);
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        return normalized;
                    }
                }
                catch
                {
                }
            }
        }

        try
        {
            trimmed = Uri.UnescapeDataString(trimmed);
        }
        catch
        {
        }

        return NormalizeFileName(trimmed);
    }

    private static byte[] DecodePercentEncodedToBytes(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Array.Empty<byte>();
        }

        using var buffer = new MemoryStream(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '%' && i + 2 < value.Length)
            {
                var hex = value.Substring(i + 1, 2);
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    buffer.WriteByte(b);
                    i += 2;
                    continue;
                }
            }

            buffer.WriteByte((byte)ch);
        }

        return buffer.ToArray();
    }
}
