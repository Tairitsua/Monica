using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;

namespace Monica.JobScheduler.Providers;

/// <summary>
/// Implements the owner aggregation half of the volatile scheduler store.
/// </summary>
public sealed partial class InMemoryJobSchedulerStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<JobOwnerSummary>> QueryOwnerSummariesAsync(
        string schedulerScopeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var owners = _definitions.Values
                .Where(definition => string.Equals(
                    definition.SchedulerScopeKey,
                    schedulerScopeKey,
                    StringComparison.Ordinal))
                .GroupBy(definition => definition.OwnerKey, StringComparer.Ordinal)
                .Select(group => new JobOwnerSummary
                {
                    OwnerKey = group.Key,
                    PresentCount = group.Count(definition => definition.IsPresent),
                    AbsentCount = group.Count(definition => !definition.IsPresent),
                    QueuedCount = 0,
                    RunningCount = 0,
                    LastObservedAtUtc = _owners.TryGetValue((schedulerScopeKey, group.Key), out var observation)
                        ? observation.Observed : group.Max(definition => definition.LastObservedAtUtc),
                    ManifestHash = observation.Hash,
                    ManifestPublishedAtUtc = observation.Published == default ? null : observation.Published
                })
                .ToDictionary(static summary => summary.OwnerKey, StringComparer.Ordinal);

            foreach (var execution in _executionInstances.Values.Where(execution => string.Equals(
                         execution.Template.SchedulerScopeKey,
                         schedulerScopeKey,
                         StringComparison.Ordinal)
                         && execution.State is JobExecutionState.Queued or JobExecutionState.Running))
            {
                var ownerKey = execution.Template.OwnerKey;
                if (owners.TryGetValue(ownerKey, out var summary))
                {
                    owners[ownerKey] = execution.State == JobExecutionState.Queued
                        ? summary with { QueuedCount = summary.QueuedCount + 1 }
                        : summary with { RunningCount = summary.RunningCount + 1 };
                }
            }

            IReadOnlyList<JobOwnerSummary> result =
                owners.Values.OrderBy(static summary => summary.OwnerKey, StringComparer.Ordinal).ToArray();
            return Task.FromResult(result);
        }
    }
}
