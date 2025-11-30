using System;
using System.IO;

namespace Diffusion.Common;

/// <summary>
/// String utility methods for common operations
/// </summary>
public static class StringUtility
{
    /// <summary>
    /// Truncate a file path for display, preserving the end of the path.
    /// Example: "C:\Users\...\Documents\Images\photo.jpg"
    /// </summary>
    /// <param name="path">The full file path</param>
    /// <param name="maxLength">Maximum length of the result</param>
    /// <param name="ellipsis">The ellipsis string to use (default: "...")</param>
    /// <returns>Truncated path or original if within maxLength</returns>
    public static string TruncatePath(string? path, int maxLength = 100, string ellipsis = "...")
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path ?? string.Empty;

        // Find a good break point (directory separator)
        var breakPoint = path.LastIndexOf(Path.DirectorySeparatorChar, maxLength);
        if (breakPoint < 0)
            breakPoint = path.LastIndexOf(Path.AltDirectorySeparatorChar, maxLength);
        
        if (breakPoint > ellipsis.Length)
        {
            return ellipsis + path[breakPoint..];
        }
        
        // Fallback: just truncate from the front
        return ellipsis + path[(path.Length - maxLength + ellipsis.Length)..];
    }
    
    /// <summary>
    /// Truncate a string to a maximum length with ellipsis
    /// </summary>
    /// <param name="text">The text to truncate</param>
    /// <param name="maxLength">Maximum length including ellipsis</param>
    /// <param name="ellipsis">The ellipsis string to use (default: "...")</param>
    /// <returns>Truncated string or original if within maxLength</returns>
    public static string Truncate(string? text, int maxLength, string ellipsis = "...")
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        
        if (maxLength <= ellipsis.Length)
            return ellipsis[..maxLength];
        
        return text[..(maxLength - ellipsis.Length)] + ellipsis;
    }
    
    /// <summary>
    /// Format a file size in human-readable format
    /// </summary>
    /// <param name="bytes">Size in bytes</param>
    /// <returns>Formatted string like "1.5 GB"</returns>
    public static string FormatFileSize(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int suffixIndex = 0;
        double size = bytes;
        
        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }
        
        return $"{size:0.##} {suffixes[suffixIndex]}";
    }
    
    /// <summary>
    /// Parse a time span from a human-readable string (e.g., "5m", "2h", "1d")
    /// </summary>
    /// <param name="input">Input string</param>
    /// <returns>TimeSpan or null if parsing fails</returns>
    public static TimeSpan? ParseTimeSpan(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        
        input = input.Trim().ToLowerInvariant();
        
        if (input.EndsWith("ms") && double.TryParse(input[..^2], out var ms))
            return TimeSpan.FromMilliseconds(ms);
        if (input.EndsWith("s") && double.TryParse(input[..^1], out var s))
            return TimeSpan.FromSeconds(s);
        if (input.EndsWith("m") && double.TryParse(input[..^1], out var m))
            return TimeSpan.FromMinutes(m);
        if (input.EndsWith("h") && double.TryParse(input[..^1], out var h))
            return TimeSpan.FromHours(h);
        if (input.EndsWith("d") && double.TryParse(input[..^1], out var d))
            return TimeSpan.FromDays(d);
        
        // Try parsing as TimeSpan directly
        if (TimeSpan.TryParse(input, out var ts))
            return ts;
        
        return null;
    }
}
