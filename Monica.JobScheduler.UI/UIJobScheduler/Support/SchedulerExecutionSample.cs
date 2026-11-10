using Monica.JobScheduler.Facades;
using Monica.Core.Results;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.UI.UIJobScheduler.Support;

/// <summary>Loads a bounded diagnostic timeline with one count and one ordered store read.</summary>
/// <remarks>The component owns authorization and lifetime. At most 2,000 records are loaded; truncation is visible.</remarks>
internal sealed record SchedulerExecutionSample(IReadOnlyList<JobExecutionInstance> Records, int TotalCount)
{
    internal bool IsTruncated => Records.Count < TotalCount;

    internal static async Task<SchedulerExecutionSample> LoadAsync(JobSchedulerFacade facade, JobExecutionQuery query, CancellationToken token)
    {
        var result = await facade.GetExecutionSampleAsync(query, token);
        token.ThrowIfCancellationRequested();
        var error = result.WithoutData();
        var sample = result.Data;
        if (result.IsFailure() || sample is null) throw new InvalidOperationException(error.Message ?? "The execution query returned no data.");
        return new(sample.Items, sample.TotalCount);
    }
}
