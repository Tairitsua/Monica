using Monica.Core.Execution;
using Monica.Profiling.ExecutionTiming.Models;

namespace Monica.Profiling.UIExecutionTiming.Support;

internal static class TimingAssessment
{
    internal static double Total(ExecutionTimingStatistics item) => item.AverageDurationMs * item.ExecutionCount;
    internal static double Age(RunningExecutionTimingInfo item, DateTimeOffset capture) => Math.Max(0, (capture - item.StartedAt).TotalMilliseconds);
    internal static bool HasBaseline(ExecutionTimingStatistics? item) => item is { ExecutionCount: >= 3, AverageDurationMs: > 0 };
    internal static bool Lingering(double age, ExecutionTimingStatistics? baseline) =>
        HasBaseline(baseline) && age >= Math.Max(1000, 2 * baseline!.AverageDurationMs);
    internal static string Drift(ExecutionTimingStatistics item)
    {
        if (HasBaseline(item))
        {
            var delta = item.LastDurationMs - item.AverageDurationMs;
            if (delta >= 50 && item.LastDurationMs >= 2 * item.AverageDurationMs) return "spike";
            if (delta <= -50 && item.LastDurationMs <= .5 * item.AverageDurationMs) return "faster";
        }
        return "steady";
    }

    internal static string Point(string key) => ExecutionOperationKey.ReadIdentity(key)?.Point ?? "direct-scope";
    internal static string Group(string key) => Point(key) switch
    {
        "mediator.request" => "mediator",
        "jobs.recurring-attempt" or "jobs.triggered-attempt" => "jobs",
        "eventbus.local-handler" or "eventbus.distributed-handler" or "eventbus.distributed-batch-handler" => "eventbus",
        "webapi.mvc-action" => "mvc",
        "hosted-service.start" or "hosted-service.stop" or "hosted-service.work-item" or "seeder.run" => "hosted",
        "direct-scope" => "direct",
        var other => other
    };

    internal static string PointRole(string key) => Group(key) switch
    {
        "mediator" or "mvc" => "primary",
        "jobs" or "hosted" => "third",
        "eventbus" => "info",
        _ => "neutral"
    };

    internal static string DriftRole(string drift) => drift switch
    {
        "spike" => "warning",
        "faster" => "success",
        _ => "neutral"
    };

    internal static string? DriftChange(ExecutionTimingStatistics item, string drift) => drift switch
    {
        "spike" => Ratio(item.LastDurationMs, item.AverageDurationMs),
        "faster" => $"{(item.LastDurationMs / item.AverageDurationMs - 1) * 100:0}%",
        _ => null
    };

    internal static string Ratio(double value, double baseline) => $"{value / baseline:N1}×";
    internal static string RunningAge(double milliseconds) => $"{milliseconds / 1000:N2} s";

    internal static string Milliseconds(double value) => value < 10 ? $"{value:N2} ms" : $"{value:N1} ms";

    internal static (string Value, string Unit) DisplayDuration(double milliseconds) => milliseconds switch
    {
        >= 10000 => ((milliseconds / 1000).ToString("N2"), "s"),
        >= 1000 => (milliseconds.ToString("N0"), "ms"),
        >= 100 => (milliseconds.ToString("N1"), "ms"),
        _ => (milliseconds.ToString("N2"), "ms")
    };

    internal static string Cumulative(double milliseconds)
    {
        if (milliseconds < 1000) return $"{milliseconds:N0} ms";
        if (milliseconds < 60000) return $"{milliseconds / 1000:N1} s";
        var seconds = (long)Math.Round(milliseconds / 1000);
        return $"{seconds / 60}m {seconds % 60:00}s";
    }

    internal static (string Prefix, string Leaf) NameParts(string name)
    {
        var last = name.LastIndexOf('.');
        var namespaceEnd = last > 0 ? name.LastIndexOf('.', last - 1) : -1;
        return namespaceEnd < 0 ? (string.Empty, name) : (name[..(namespaceEnd + 1)], name[(namespaceEnd + 1)..]);
    }

    internal static string ShortInvocation(Guid id, IReadOnlyList<RunningExecutionTimingInfo> running)
    {
        var value = id.ToString("D");
        var prefix = value[..8];
        return running.Count(run => run.InvocationId.ToString("D").StartsWith(prefix, StringComparison.Ordinal)) > 1
            ? $"#{prefix}…{value[^4..]}"
            : $"#{prefix}";
    }

    internal readonly record struct Axis(double Ceiling, IReadOnlyList<double> Ticks);

    internal static Axis CreateAxis(double maximum)
    {
        var extent = Math.Max(10, maximum);
        var rawStep = extent / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var step = ((rawStep / magnitude) switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10
        }) * magnitude;
        var intervals = (int)Math.Ceiling(extent / step);
        return new Axis(intervals * step, Enumerable.Range(0, intervals + 1).Select(index => index * step).ToArray());
    }

    internal static string AxisTick(double value, double ceiling) => value == 0 ? "0"
        : ceiling >= 5000 ? $"{value / 1000:0.#} s" : $"{value:N0} ms";

    internal static string Duration(double ms) => ms < 1000 ? $"{ms:0.##} ms" : ms < 60000 ? $"{ms/1000:0.##} s" : $"{ms/60000:0.##} min";
    internal static string Percent(double value, double maximum) => FormattableString.Invariant($"{Math.Clamp(maximum > 0 ? value / maximum : 0, 0, 1)*100:0.###}%");
}
