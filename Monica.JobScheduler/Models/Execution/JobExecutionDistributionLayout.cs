namespace Monica.JobScheduler.Models.Execution;

/// <summary>Shares bucket arithmetic across the memory and relational providers.</summary>
internal sealed record JobExecutionDistributionLayout(long StartTicks, long EndTicks, long BucketTicks, int Count, bool Completed)
{
    internal static JobExecutionDistributionLayout Create(JobExecutionQuery query, int bucketCount, long? first, long? last, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bucketCount, 200);
        var completed = query.CompletedAfterUtc is not null || query.CompletedBeforeUtc is not null;
        var lower = completed ? query.CompletedAfterUtc : query.CreatedAfterUtc;
        var upper = completed ? query.CompletedBeforeUtc : query.CreatedBeforeUtc;
        var final = (upper?.UtcTicks ?? last ?? now.UtcTicks);
        var end = final < DateTimeOffset.MaxValue.UtcTicks ? final + 1 : final;
        var start = lower is { } bound && bound > DateTimeOffset.MinValue ? bound.UtcTicks : first ?? Math.Max(0, end - TimeSpan.TicksPerHour);
        if (start >= end) { start = Math.Min(start, DateTimeOffset.MaxValue.UtcTicks - 1); end = start + 1; }
        var width = Math.Max(1, (end - start + bucketCount - 1) / bucketCount);
        return new(start, end, width, (int)((end - start + width - 1) / width), completed);
    }

    internal JobExecutionDistribution Build(IEnumerable<(long Bucket, JobExecutionState State, long Count)> groups)
    {
        var cells = groups.GroupBy(item => (Bucket: (int)Math.Clamp(item.Bucket, 0, Count - 1), item.State))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count));
        var states = Enum.GetValues<JobExecutionState>();
        var buckets = Enumerable.Range(0, Count).Select(index => new JobExecutionDistributionBucket
        {
            StartTimeUtc = new DateTimeOffset(StartTicks + index * BucketTicks, TimeSpan.Zero),
            EndTimeUtc = new DateTimeOffset(Math.Min(EndTicks, StartTicks + (index + 1) * BucketTicks), TimeSpan.Zero),
            Counts = states.ToDictionary(state => state, state => cells.GetValueOrDefault((index, state)))
        }).ToArray();
        return new() { StartTimeUtc = new DateTimeOffset(StartTicks, TimeSpan.Zero), EndTimeUtc = new DateTimeOffset(EndTicks, TimeSpan.Zero), UsesCompletionTime = Completed, Buckets = buckets, StateTotals = states.ToDictionary(state => state, state => buckets.Sum(bucket => bucket.Counts[state])) };
    }
}
