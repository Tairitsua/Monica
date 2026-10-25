using System.Globalization;
using Monica.Profiling.ExecutionTiming.Models;
using Monica.Tool.Text;

namespace Monica.Profiling.UIExecutionTiming.Support;

internal static class ExecutionTimingDisplayFormatter
{

    public static string FormatMachineDateTime(DateTimeOffset? timestamp) =>
        timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string FormatDuration(double milliseconds)
    {
        return $"{milliseconds:0.##}ms";
    }

    public static string FormatMemory(long? bytes)
    {
        return bytes.HasValue ? bytes.Value.FormatByteSize() : "—";
    }

    public static string FormatRatePerMinute(ExecutionTimingStatistics statistics, DateTimeOffset asOf)
    {
        var elapsedMinutes = Math.Max((asOf - statistics.FirstRecordedAt).TotalMinutes, 1d);
        return $"{statistics.ExecutionCount / elapsedMinutes:0.##}";
    }

    public static string FormatRunningDuration(RunningExecutionTimingInfo operation, DateTimeOffset asOf)
    {
        return $"{Math.Max((asOf - operation.StartedAt).TotalSeconds, 0d):0.##}s";
    }
}
