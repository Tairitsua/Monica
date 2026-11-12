using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Derives the accepted prototype's attention rules from one explicitly bounded loaded sample.</summary>
internal sealed record SchedulerJobEvidence(JobDefinition Definition, IReadOnlyList<JobExecutionInstance> Records,
    int FailureStreak, int FailedCount, int SkippedCount, bool IsOverrun, bool IsSkipping, bool IsIntermittent, bool IsComplete)
{
    internal bool NeedsAttention => FailureStreak > 0 || IsOverrun || IsSkipping || IsIntermittent;
    internal bool IsAssessed => IsComplete && (Records.Count > 0 || Definition.Declaration.JobType == JobType.Triggered);
    internal bool FailureStreakIsLowerBound => FailureStreak > 0 && (!IsComplete
        || !Records.Any(record => record.State is JobExecutionState.Succeeded or JobExecutionState.Cancelled));
    internal string Tone => !Definition.IsPresent ? "absent" : Definition.IsDisabled ? "paused"
        : FailureStreak > 0 || IsIntermittent ? "error" : IsOverrun || IsSkipping ? "warning"
        : !IsAssessed ? "neutral" : Definition.Declaration.JobType == JobType.Triggered ? "primary" : "success";

    internal static SchedulerJobEvidence Create(JobDefinition definition, IEnumerable<JobExecutionInstance> records, DateTimeOffset observedAtUtc, bool isComplete = true)
    {
        var ordered = records.OrderByDescending(record => record.CreatedAtUtc).ThenBy(record => record.InstanceId, StringComparer.Ordinal).ToArray();
        var decided = ordered.Where(record => record.State is JobExecutionState.Succeeded or JobExecutionState.Failed).ToArray();
        var streak = ordered.Where(record => record.State is JobExecutionState.Succeeded or JobExecutionState.Failed or JobExecutionState.Cancelled)
            .TakeWhile(record => record.State == JobExecutionState.Failed).Count();
        var failed = decided.Count(record => record.State == JobExecutionState.Failed);
        var historical = ordered.Where(record => record.IsTerminal)
            .Select(record => SchedulerTimePresentation.GetExecutionDuration(record, observedAtUtc)).OfType<TimeSpan>()
            .Select(value => value.TotalMilliseconds).Order().ToArray();
        var threshold = historical.Length == 0 ? double.PositiveInfinity
            : historical.Length < 20 ? historical[(historical.Length - 1) / 2] * 2
            : historical[Math.Max(0, (int)Math.Ceiling(historical.Length * .95) - 1)];
        var overrun = isComplete && historical.Length >= 3 && ordered.Any(record => record.State == JobExecutionState.Running
            && SchedulerTimePresentation.GetExecutionDuration(record, observedAtUtc) is { TotalSeconds: > 30 } elapsed
            && elapsed.TotalMilliseconds > threshold);
        var skipped = ordered.Count(record => record.State == JobExecutionState.Skipped);
        var scheduleRecords = ordered.Count(record => record.Origin == JobExecutionOrigin.RecurringSchedule);
        var skipping = isComplete && SchedulerCadence.Bucket(definition, observedAtUtc) != "sub" && skipped >= 3
            && scheduleRecords > 0 && skipped / (double)scheduleRecords >= .1;
        var intermittent = isComplete && decided.FirstOrDefault()?.State == JobExecutionState.Succeeded
            && failed >= 3 && failed / (double)decided.Length >= .25;
        return new(definition, ordered, streak, failed, skipped, overrun, skipping, intermittent, isComplete);
    }
}
