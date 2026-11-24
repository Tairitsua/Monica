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

    internal static string Duration(double ms) => ms < 1000 ? $"{ms:0.##} ms" : ms < 60000 ? $"{ms/1000:0.##} s" : $"{ms/60000:0.##} min";
    internal static string Percent(double value, double maximum) => FormattableString.Invariant($"{Math.Clamp(maximum > 0 ? value / maximum : 0, 0, 1)*100:0.###}%");
}
