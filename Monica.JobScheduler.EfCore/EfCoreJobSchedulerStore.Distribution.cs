using System.Data;
using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobExecutionDistribution> GetExecutionDistributionAsync(JobExecutionQuery query, int bucketCount = 48, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bucketCount, 200);
        return ReadAsync(async (dbContext, token) =>
        {
            await using var transaction = dbContext.Database.IsRelational() ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var completed = query.CompletedAfterUtc is not null || query.CompletedBeforeUtc is not null;
            var rows = FilterExecutions(dbContext, query with { States = null }).Select(execution => new { At = completed ? execution.CompletedAtUtcTicks : (long?)execution.CreatedAtUtcTicks, execution.State }).Where(row => row.At != null);
            var bounds = await rows.GroupBy(row => 1).Select(group => new { First = group.Min(row => row.At), Last = group.Max(row => row.At) }).SingleOrDefaultAsync(token);
            var layout = JobExecutionDistributionLayout.Create(query, bucketCount, bounds?.First, bounds?.Last, await GetUtcNowAsync(dbContext, token));
            var start = layout.StartTicks; var width = layout.BucketTicks;
            var groups = await rows.GroupBy(row => new { Bucket = (row.At!.Value - start) / width, row.State }).Select(group => new { group.Key.Bucket, group.Key.State, Count = group.LongCount() }).ToArrayAsync(token);
            var result = layout.Build(groups.Select(group => (group.Bucket, group.State, group.Count)));
            if (transaction is not null) await transaction.CommitAsync(token);
            return result;
        }, cancellationToken);
    }
}
