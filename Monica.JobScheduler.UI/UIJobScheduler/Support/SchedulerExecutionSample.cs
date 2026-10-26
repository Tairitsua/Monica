using Monica.JobScheduler.Facades;
using Monica.Core.Results;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Loads a bounded diagnostic timeline through the same facade query used by the native ledger.</summary>
/// <remarks>The component owns authorization and lifetime. At most 2,000 records are loaded; truncation is visible.</remarks>
internal sealed record SchedulerExecutionSample(IReadOnlyList<JobExecutionInstance> Records, int TotalCount)
{
    internal bool IsTruncated => Records.Count < TotalCount;

    internal static async Task<SchedulerExecutionSample> LoadAsync(JobSchedulerFacade facade, JobExecutionQuery query, CancellationToken token)
    {
        const int pageSize = 200, pageLimit = 10;
        var records = new List<JobExecutionInstance>();
        var total = 0;
        for (var pageNumber = 1; pageNumber <= pageLimit; pageNumber++)
        {
            var result = await facade.QueryExecutionsAsync(query with
            {
                PageNumber = pageNumber, PageSize = pageSize,
                SortField = JobExecutionSortField.CreatedAtUtc, SortDescending = true
            }, token);
            token.ThrowIfCancellationRequested();
            if (result.IsFailed(out var error, out var page)) throw new InvalidOperationException(error.Message);
            total = page.TotalCount;
            records.AddRange(page.Items);
            if (page.Items.Count < pageSize || records.Count >= total) break;
        }
        return new(records.DistinctBy(record => record.InstanceId, StringComparer.Ordinal).ToArray(), total);
    }
}
