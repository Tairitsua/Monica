using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Providers;

public sealed partial class InMemoryJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobExecutionDistribution> GetExecutionDistributionAsync(JobExecutionQuery query, int bucketCount = 48, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var completed = query.CompletedAfterUtc is not null || query.CompletedBeforeUtc is not null;
            var rows = FilterExecutions(query with { States = null }).Select(execution => (At: completed ? execution.CompletedAtUtc?.UtcTicks : execution.CreatedAtUtc.UtcTicks, execution.State)).Where(row => row.At is not null).ToArray();
            var layout = JobExecutionDistributionLayout.Create(query, bucketCount, rows.Select(row => row.At).Min(), rows.Select(row => row.At).Max(), UtcNow);
            var groups = rows.GroupBy(row => (Bucket: (row.At!.Value - layout.StartTicks) / layout.BucketTicks, row.State)).Select(group => (group.Key.Bucket, group.Key.State, Count: group.LongCount()));
            return Task.FromResult(layout.Build(groups));
        }
    }
}
