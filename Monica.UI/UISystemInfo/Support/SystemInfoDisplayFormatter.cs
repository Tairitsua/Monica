using System.Globalization;

namespace Monica.UI.UISystemInfo.Support;

/// <summary>
/// Formats System Info evidence consistently without owning localized labels or host-specific values.
/// </summary>
public static class SystemInfoDisplayFormatter
{
    private const long KIBIBYTE = 1024;
    private const long MEBIBYTE = KIBIBYTE * 1024;
    private const long GIBIBYTE = MEBIBYTE * 1024;

    /// <summary>Formats an elapsed duration for compact operational display.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}.{duration:hh\\:mm\\:ss}"
            : duration.TotalHours >= 1
                ? duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Formats a byte quantity with the most useful binary scale.</summary>
    public static string FormatScaledBytes(long bytes, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var nonNegativeBytes = Math.Max(0, bytes);
        return nonNegativeBytes switch
        {
            >= GIBIBYTE => $"{(nonNegativeBytes / (double)GIBIBYTE).ToString("N2", culture)} GiB",
            >= MEBIBYTE => $"{(nonNegativeBytes / (double)MEBIBYTE).ToString("N1", culture)} MiB",
            >= KIBIBYTE => $"{(nonNegativeBytes / (double)KIBIBYTE).ToString("N1", culture)} KiB",
            _ => $"{nonNegativeBytes.ToString("N0", culture)} B"
        };
    }

    /// <summary>Formats an exact byte count with culture-aware digit grouping.</summary>
    public static string FormatByteCount(long bytes, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return $"{Math.Max(0, bytes).ToString("N0", culture)} B";
    }

}
