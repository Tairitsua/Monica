using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.Models.Execution;
using Monica.JobScheduler.Models.Operations;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<JobOwnerSummary>> QueryOwnerSummariesAsync(
        string schedulerScopeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        return ReadAsync(async (dbContext, token) =>
        {
            // Definitions anchor the aggregation: an owner exists in the catalog only while at least one of its
            // definitions is retained, so queue counts are folded in with a dictionary merge instead of a join
            // that could surface execution-only owners.
            var definitionRows = await dbContext.Definitions.AsNoTracking()
                .Where(definition => definition.SchedulerScopeKey == schedulerScopeKey)
                .GroupBy(definition => definition.OwnerKey)
                .Select(group => new
                {
                    OwnerKey = group.Key,
                    PresentCount = group.Count(definition => definition.IsPresent),
                    AbsentCount = group.Count(definition => !definition.IsPresent),
                    LastObservedAtUtcTicks = group.Max(definition => definition.LastObservedAtUtcTicks)
                })
                .ToListAsync(token);
            var summaries = definitionRows.ToDictionary(
                static row => row.OwnerKey,
                static row => new JobOwnerSummary
                {
                    OwnerKey = row.OwnerKey,
                    PresentCount = row.PresentCount,
                    AbsentCount = row.AbsentCount,
                    QueuedCount = 0,
                    RunningCount = 0,
                    LastObservedAtUtc = FromTicks(row.LastObservedAtUtcTicks)
                },
                StringComparer.Ordinal);

            var ownerRows = await dbContext.SchedulerOwners.AsNoTracking()
                .Where(row => row.SchedulerScopeKey == schedulerScopeKey).ToListAsync(token);
            foreach (var owner in ownerRows)
            {
                if (summaries.TryGetValue(owner.OwnerKey, out var summary))
                    summaries[owner.OwnerKey] = summary with
                    {
                        LastObservedAtUtc = FromTicks(owner.LastObservedAtUtcTicks),
                        ManifestHash = owner.ManifestHash,
                        ManifestPublishedAtUtc = FromTicks(owner.ManifestPublishedAtUtcTicks)
                    };
            }

            var executionRows = await dbContext.Executions.AsNoTracking()
                .Where(execution => execution.SchedulerScopeKey == schedulerScopeKey
                                    && (execution.State == JobExecutionState.Queued
                                        || execution.State == JobExecutionState.Running))
                .GroupBy(execution => new { execution.OwnerKey, execution.State })
                .Select(group => new { group.Key.OwnerKey, group.Key.State, Count = group.Count() })
                .ToListAsync(token);
            foreach (var row in executionRows)
            {
                if (!summaries.TryGetValue(row.OwnerKey, out var summary))
                {
                    continue;
                }

                summaries[row.OwnerKey] = row.State == JobExecutionState.Queued
                    ? summary with { QueuedCount = summary.QueuedCount + row.Count }
                    : summary with { RunningCount = summary.RunningCount + row.Count };
            }

            return (IReadOnlyList<JobOwnerSummary>)summaries.Values
                .OrderBy(static summary => summary.OwnerKey, StringComparer.Ordinal)
                .ToArray();
        }, cancellationToken);
    }
}
