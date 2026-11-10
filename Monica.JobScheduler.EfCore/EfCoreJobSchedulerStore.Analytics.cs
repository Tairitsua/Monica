using System.Data;
using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.EfCore.Entities;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Analytics;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobExecutionAnalyticsSnapshot> GetExecutionAnalyticsAsync(
        string schedulerScopeKey,
        JobExecutionAnalyticsQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(schedulerScopeKey, nameof(schedulerScopeKey));
        ArgumentNullException.ThrowIfNull(query);
        var range = query.ValidateAndNormalize();
        return ReadAsync(async (dbContext, token) =>
        {
            // Analytics compose several bounded aggregates; one read transaction keeps every panel on the same
            // database snapshot while workers concurrently advance execution state.
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, token)
                : null;
            var snapshot = await GetExecutionAnalyticsCoreAsync(
                dbContext,
                schedulerScopeKey,
                query,
                range,
                token);
            if (transaction is not null)
            {
                await transaction.CommitAsync(token);
            }

            return snapshot;
        }, cancellationToken);
    }

    private static async Task<JobExecutionAnalyticsSnapshot> GetExecutionAnalyticsCoreAsync(
        JobSchedulerDbContext dbContext,
        string schedulerScopeKey,
        JobExecutionAnalyticsQuery query,
        JobExecutionAnalyticsRange range,
        CancellationToken cancellationToken)
    {
        var scoped = dbContext.Executions.AsNoTracking()
            .Where(item => item.SchedulerScopeKey == schedulerScopeKey);
        if (query.OwnerKey is not null)
        {
            scoped = scoped.Where(item => item.OwnerKey == query.OwnerKey);
        }
        if (query.JobKey is not null)
        {
            scoped = scoped.Where(item => item.JobKey == query.JobKey);
        }

        var startTicks = ToTicks(range.StartTimeUtc);
        var endTicks = ToTicks(range.EndTimeUtc);
        var cohortGroups = await scoped
            .Where(item => item.CreatedAtUtcTicks >= startTicks && item.CreatedAtUtcTicks < endTicks)
            .GroupBy(static item => item.State)
            .Select(static group => new AnalyticsStateCount(group.Key, group.LongCount()))
            .ToArrayAsync(cancellationToken);
        var cohortByState = cohortGroups.ToDictionary(static item => item.State, static item => item.Count);
        IReadOnlyDictionary<JobExecutionState, long> stateTotals = Enum.GetValues<JobExecutionState>()
            .ToDictionary(state => state, state => cohortByState.GetValueOrDefault(state));

        var completions = scoped.Where(item =>
            item.CompletedAtUtcTicks != null
            && item.CompletedAtUtcTicks >= startTicks
            && item.CompletedAtUtcTicks < endTicks
            && (item.State == JobExecutionState.Succeeded
                || item.State == JobExecutionState.Failed
                || item.State == JobExecutionState.Cancelled
                || item.State == JobExecutionState.Skipped));
        var outcomeRows = await completions
            .GroupBy(static item => new { item.State, item.Origin })
            .Select(static group => new AnalyticsOutcomeCount(
                group.Key.State,
                group.Key.Origin,
                group.LongCount(),
                group.LongCount(item => item.StartedAtUtcTicks != null)))
            .ToArrayAsync(cancellationToken);
        var outcomes = outcomeRows
            .GroupBy(static item => item.State)
            .ToDictionary(static group => group.Key, static group => group.Sum(static item => item.Count));
        var succeededCount = outcomes.GetValueOrDefault(JobExecutionState.Succeeded);
        var failedCount = outcomes.GetValueOrDefault(JobExecutionState.Failed);
        var completedTerminalCount = outcomeRows.Sum(static item => item.Count);
        var recurringScheduleDispositionCount = outcomeRows
            .Where(static item =>
                item.Origin == JobExecutionOrigin.RecurringSchedule
                && item.State is (JobExecutionState.Succeeded
                    or JobExecutionState.Failed
                    or JobExecutionState.Skipped))
            .Sum(static item => item.Count);
        var fulfilledRecurringScheduleCount = outcomeRows
            .Where(static item =>
                item.Origin == JobExecutionOrigin.RecurringSchedule
                && item.State is (JobExecutionState.Succeeded or JobExecutionState.Failed))
            .Sum(static item => item.Count);
        var executedTerminalCount = outcomeRows.Sum(static item => item.ExecutedCount);
        var duration = await GetDurationStatisticsAsync(dbContext, schedulerScopeKey, query, startTicks, endTicks, cancellationToken);
        var trend = await GetTrendAsync(
            completions,
            range,
            startTicks,
            cancellationToken);
        var (topByVolume, topByFailures) = await GetJobRankingsAsync(completions, query.TopJobLimit, cancellationToken);
        var measurableCompletions = completions
            .Where(static execution =>
                execution.StartedAtUtcTicks != null
                && execution.CompletedAtUtcTicks >= execution.StartedAtUtcTicks);
        // Slowest execution per job in two phases: a plain GROUP BY + MAX reduces each job to its peak duration,
        // then an exact-equality fetch loads all maximum-duration ties. Choose one ordinal instance winner per job
        // before global ranking and limiting. Correlated argmax subqueries are not translated reliably by every provider.
        var slowestBoundaries = (await measurableCompletions
            .GroupBy(static execution => new { execution.OwnerKey, execution.JobKey })
            .Select(static group => new
            {
                group.Key.OwnerKey,
                group.Key.JobKey,
                DurationTicks = group.Max(static execution =>
                    execution.CompletedAtUtcTicks!.Value - execution.StartedAtUtcTicks!.Value)
            })
            .ToArrayAsync(cancellationToken))
            .OrderByDescending(static row => row.DurationTicks)
            .ThenBy(static row => row.OwnerKey, StringComparer.Ordinal)
            .ThenBy(static row => row.JobKey, StringComparer.Ordinal)
            .Take(query.SlowestExecutionLimit)
            .ToArray();
        var slowestRows = (await measurableCompletions
                .Where(CreateBoundaryPredicate(
                    slowestBoundaries.Select(static row =>
                        (new JobId(row.OwnerKey, row.JobKey), row.DurationTicks)),
                    static execution => execution.CompletedAtUtcTicks!.Value - execution.StartedAtUtcTicks!.Value))
                .Select(static execution => new
                {
                    execution.InstanceId,
                    execution.TemplateJson,
                    execution.OwnerKey,
                    execution.JobKey,
                    execution.State,
                    StartedAtUtcTicks = execution.StartedAtUtcTicks!.Value,
                    CompletedAtUtcTicks = execution.CompletedAtUtcTicks!.Value,
                    DurationTicks = execution.CompletedAtUtcTicks.Value - execution.StartedAtUtcTicks.Value
                })
                .ToArrayAsync(cancellationToken))
            .GroupBy(static execution => new JobId(execution.OwnerKey, execution.JobKey))
            .Select(static group => group.OrderBy(static execution => execution.InstanceId, StringComparer.Ordinal).First())
            .OrderByDescending(static execution => execution.DurationTicks)
            .ThenBy(static execution => execution.OwnerKey, StringComparer.Ordinal)
            .ThenBy(static execution => execution.JobKey, StringComparer.Ordinal)
            .ThenBy(static execution => execution.InstanceId, StringComparer.Ordinal)
            .Take(query.SlowestExecutionLimit)
            .ToArray();

        return new JobExecutionAnalyticsSnapshot
        {
            StartTimeUtc = range.StartTimeUtc,
            EndTimeUtc = range.EndTimeUtc,
            BucketSize = query.BucketSize,
            OwnerKey = query.OwnerKey,
            JobKey = query.JobKey,
            StateTotals = stateTotals,
            CompletedTerminalCount = completedTerminalCount,
            ExecutedTerminalCount = executedTerminalCount,
            ExecutedThroughputPerHour = executedTerminalCount / (range.EndTimeUtc - range.StartTimeUtc).TotalHours,
            Reliability = JobExecutionAnalyticsMath.Ratio(succeededCount, succeededCount + failedCount),
            RecurringScheduleDispositionCount = recurringScheduleDispositionCount,
            RecurringScheduleFulfillment = JobExecutionAnalyticsMath.Ratio(
                fulfilledRecurringScheduleCount,
                recurringScheduleDispositionCount),
            Duration = duration,
            Trend = trend,
            TopJobsByVolume = topByVolume,
            TopJobsByFailures = topByFailures,
            SlowestExecutions = slowestRows.Select(static item => new JobExecutionAnalyticsSlowExecution
            {
                InstanceId = item.InstanceId,
                JobName = Deserialize<JobExecutionTemplate>(item.TemplateJson).JobName,
                OwnerKey = item.OwnerKey,
                JobKey = item.JobKey,
                State = item.State,
                StartedAtUtc = FromTicks(item.StartedAtUtcTicks),
                CompletedAtUtc = FromTicks(item.CompletedAtUtcTicks)
            }).ToArray()
        };
    }

    private static async Task<JobExecutionDurationStatistics> GetDurationStatisticsAsync(
        JobSchedulerDbContext dbContext,
        string scope,
        JobExecutionAnalyticsQuery query,
        long startTicks,
        long endTicks,
        CancellationToken cancellationToken)
    {
        var parameters = new List<object> { scope, startTicks, endTicks };
        var filter = "";
        if (query.OwnerKey is not null)
        {
            filter += " AND \"OwnerKey\" = {" + parameters.Count + "}";
            parameters.Add(query.OwnerKey);
        }
        if (query.JobKey is not null)
        {
            filter += " AND \"JobKey\" = {" + parameters.Count + "}";
            parameters.Add(query.JobKey);
        }

        // One scan and one sort compute all exact nearest-rank percentiles. Integer cross-products avoid
        // provider-dependent bigint division and keep rank boundaries exact. Only one aggregate row is transferred.
        var sql = """
            SELECT COUNT(*) AS "Count", MIN(d) AS "Minimum", MAX(d) AS "Maximum",
                   AVG(CAST(d AS double precision)) AS "Average",
                   MIN(CASE WHEN r * 100 >= n * 50 THEN d END) AS "P50",
                   MIN(CASE WHEN r * 100 >= n * 90 THEN d END) AS "P90",
                   MIN(CASE WHEN r * 100 >= n * 95 THEN d END) AS "P95",
                   MIN(CASE WHEN r * 100 >= n * 99 THEN d END) AS "P99"
            FROM (SELECT d, ROW_NUMBER() OVER (ORDER BY d) AS r, COUNT(*) OVER () AS n
                  FROM (SELECT "CompletedAtUtcTicks" - "StartedAtUtcTicks" AS d FROM
            """ + " " + TableSql<JobExecutionEntity>(dbContext) + """

                    WHERE "SchedulerScopeKey" = {0}
                      AND "CompletedAtUtcTicks" >= {1} AND "CompletedAtUtcTicks" < {2}
                      AND "StartedAtUtcTicks" IS NOT NULL AND "CompletedAtUtcTicks" >= "StartedAtUtcTicks"
                      AND "State" IN ('Succeeded', 'Failed', 'Cancelled', 'Skipped')
            """ + filter + ") measured) ranked";
        var row = await dbContext.Database.SqlQueryRaw<AnalyticsDurationRow>(sql, parameters.ToArray())
            .SingleAsync(cancellationToken);
        if (row.Count == 0) return new JobExecutionDurationStatistics();
        return new JobExecutionDurationStatistics
        {
            Count = row.Count,
            Minimum = TimeSpan.FromTicks(row.Minimum!.Value),
            Average = TimeSpan.FromTicks(checked((long)row.Average!.Value)),
            P50 = TimeSpan.FromTicks(row.P50!.Value),
            P90 = TimeSpan.FromTicks(row.P90!.Value),
            P95 = TimeSpan.FromTicks(row.P95!.Value),
            P99 = TimeSpan.FromTicks(row.P99!.Value),
            Maximum = TimeSpan.FromTicks(row.Maximum!.Value)
        };
    }

    private static async Task<IReadOnlyList<JobExecutionAnalyticsBucket>> GetTrendAsync(
        IQueryable<JobExecutionEntity> completions,
        JobExecutionAnalyticsRange range,
        long startTicks,
        CancellationToken cancellationToken)
    {
        var bucketTicks = range.BucketDuration.Ticks;
        var rows = await completions
            .Select(item => new
            {
                Offset = item.CompletedAtUtcTicks!.Value - startTicks,
                item.State,
                WasExecuted = item.StartedAtUtcTicks != null
            })
            // Remove the remainder before decimal division so GaussDB returns an exact integer bucket key.
            // Casting a fractional quotient directly to bigint rounds on PostgreSQL-family servers.
            .GroupBy(item => (long)((decimal)(item.Offset - item.Offset % bucketTicks) / bucketTicks))
            .Select(static group => new AnalyticsTrendRow(
                group.Key,
                group.LongCount(item => item.State == JobExecutionState.Succeeded),
                group.LongCount(item => item.State == JobExecutionState.Failed),
                group.LongCount(item => item.State == JobExecutionState.Skipped),
                group.LongCount(item => item.State == JobExecutionState.Cancelled),
                group.LongCount(item => item.WasExecuted)))
            .ToArrayAsync(cancellationToken);
        var byBucket = rows.ToDictionary(static item => item.BucketIndex);
        return Enumerable.Range(0, range.BucketCount).Select(index =>
        {
            byBucket.TryGetValue(index, out var row);
            var start = range.StartTimeUtc.AddTicks(range.BucketDuration.Ticks * index);
            return new JobExecutionAnalyticsBucket
            {
                StartTimeUtc = start,
                EndTimeUtc = Min(start.Add(range.BucketDuration), range.EndTimeUtc),
                SucceededCount = row?.SucceededCount ?? 0,
                FailedCount = row?.FailedCount ?? 0,
                SkippedCount = row?.SkippedCount ?? 0,
                CancelledCount = row?.CancelledCount ?? 0,
                ExecutedTerminalCount = row?.ExecutedTerminalCount ?? 0
            };
        }).ToArray();
    }

    private static async Task<(IReadOnlyList<JobExecutionAnalyticsJobRank> Volume, IReadOnlyList<JobExecutionAnalyticsJobRank> Failures)>
        GetJobRankingsAsync(IQueryable<JobExecutionEntity> completions, int limit, CancellationToken cancellationToken)
    {
        // Carry counts and the latest-template boundary in the bounded ranking queries; both panels then share
        // one template fetch instead of rescanning selected jobs for each aggregate independently.
        var grouped = completions
            .GroupBy(static item => new { item.OwnerKey, item.JobKey })
            .Select(static group => new AnalyticsJobRankRow
            {
                OwnerKey = group.Key.OwnerKey,
                JobKey = group.Key.JobKey,
                SucceededCount = group.LongCount(item => item.State == JobExecutionState.Succeeded),
                FailedCount = group.LongCount(item => item.State == JobExecutionState.Failed),
                SkippedCount = group.LongCount(item => item.State == JobExecutionState.Skipped),
                CancelledCount = group.LongCount(item => item.State == JobExecutionState.Cancelled),
                CompletedAtUtcTicks = group.Max(item => item.CompletedAtUtcTicks!.Value)
            });
        var volume = await grouped
            .OrderByDescending(item => item.SucceededCount + item.FailedCount + item.SkippedCount + item.CancelledCount)
            .ThenBy(item => item.OwnerKey).ThenBy(item => item.JobKey)
            .Take(limit).ToArrayAsync(cancellationToken);
        var failures = await grouped.Where(item => item.FailedCount > 0)
            .OrderByDescending(item => item.FailedCount)
            .ThenBy(item => item.OwnerKey).ThenBy(item => item.JobKey)
            .Take(limit).ToArrayAsync(cancellationToken);
        var selected = volume.Concat(failures).DistinctBy(item => new JobId(item.OwnerKey, item.JobKey)).ToArray();
        if (selected.Length == 0) return ([], []);

        var latestTemplates = await completions
            .Where(CreateBoundaryPredicate(selected.Select(item =>
                    (new JobId(item.OwnerKey, item.JobKey), item.CompletedAtUtcTicks)),
                static item => item.CompletedAtUtcTicks!.Value))
            .Select(static item => new { item.OwnerKey, item.JobKey, item.InstanceId, item.TemplateJson })
            .ToArrayAsync(cancellationToken);
        var names = latestTemplates.GroupBy(item => new JobId(item.OwnerKey, item.JobKey))
            .ToDictionary(group => group.Key, group => Deserialize<JobExecutionTemplate>(group
                .OrderBy(item => item.InstanceId, StringComparer.Ordinal).First().TemplateJson).JobName);
        JobExecutionAnalyticsJobRank[] Map(IEnumerable<AnalyticsJobRankRow> rows) => rows.Select(row => new JobExecutionAnalyticsJobRank
        {
            JobName = names[new JobId(row.OwnerKey, row.JobKey)],
            OwnerKey = row.OwnerKey,
            JobKey = row.JobKey,
            SucceededCount = row.SucceededCount,
            FailedCount = row.FailedCount,
            SkippedCount = row.SkippedCount,
            CancelledCount = row.CancelledCount
        }).ToArray();
        return (Map(volume), Map(failures));
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private sealed record AnalyticsStateCount(JobExecutionState State, long Count);

    private sealed record AnalyticsOutcomeCount(
        JobExecutionState State,
        JobExecutionOrigin Origin,
        long Count,
        long ExecutedCount);

    private sealed record AnalyticsTrendRow(
        long BucketIndex,
        long SucceededCount,
        long FailedCount,
        long SkippedCount,
        long CancelledCount,
        long ExecutedTerminalCount);

    private sealed class AnalyticsJobRankRow
    {
        public required string OwnerKey { get; init; }
        public required string JobKey { get; init; }
        public long SucceededCount { get; init; }
        public long FailedCount { get; init; }
        public long SkippedCount { get; init; }
        public long CancelledCount { get; init; }
        public long CompletedAtUtcTicks { get; init; }
    }

    private sealed record AnalyticsDurationRow(
        long Count, long? Minimum, long? Maximum, double? Average,
        long? P50, long? P90, long? P95, long? P99);
}
