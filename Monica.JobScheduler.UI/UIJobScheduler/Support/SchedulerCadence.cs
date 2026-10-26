using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Projects declared schedules into the catalog's ordered cadence groups without assuming fixed calendar intervals.</summary>
internal static class SchedulerCadence
{
    internal static readonly string[] Buckets = ["sub", "1m", "5m", "30m", "1h", "hours", "1d", "1mo", "demand"];

    internal static string Bucket(JobDefinition definition, DateTimeOffset observedAtUtc)
    {
        if (definition.Declaration.JobType == JobType.Triggered) return "demand";
        if (definition.EffectiveConfiguration.Schedule is not { } schedule) return "1mo";
        var preview = CronPolicyEditor.Evaluate(schedule.CronExpression, schedule.TimeZoneId,
            schedule.StartTimeUtc, schedule.EndTimeUtc, observedAtUtc);
        if (preview.Occurrences.Count < 2) return "1mo";
        var interval = preview.Occurrences[1].Utc - preview.Occurrences[0].Utc;
        return interval.TotalMinutes < 1 ? "sub" : interval.TotalMinutes < 5 ? "1m"
            : interval.TotalMinutes < 30 ? "5m" : interval.TotalHours < 1 ? "30m"
            : interval.TotalHours < 2 ? "1h" : interval.TotalHours < 24 ? "hours"
            : interval.TotalHours < 48 ? "1d" : "1mo";
    }
}
