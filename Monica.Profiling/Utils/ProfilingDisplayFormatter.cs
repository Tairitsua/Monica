namespace Monica.Profiling.Utils;

internal static class ProfilingDisplayFormatter
{
    public static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var counter = 0;
        decimal number = bytes;

        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }

        return $"{number:N1} {suffixes[counter]}";
    }

    public static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{bytesPerSecond:F0} B/s";
        }

        if (bytesPerSecond < 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024:F1} KB/s";
        }

        return $"{bytesPerSecond / 1024 / 1024:F1} MB/s";
    }

    public static string FormatCount(long count)
    {
        if (count < 1000)
        {
            return count.ToString();
        }

        if (count < 1_000_000)
        {
            return $"{count / 1000.0:F1}K";
        }

        return $"{count / 1_000_000.0:F1}M";
    }

}
