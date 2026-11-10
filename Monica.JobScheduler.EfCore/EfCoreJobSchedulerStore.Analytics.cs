using System.Data;
using System.Linq.Expressions;
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
                group.LongCount()))
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
        var executedTerminalCount = await completions.LongCountAsync(
            static item => item.StartedAtUtcTicks != null,
            cancellationToken);

        var durationTicks = completions
            .Where(static item =>
                item.StartedAtUtcTicks != null
                && item.CompletedAtUtcTicks >= item.StartedAtUtcTicks)
            .Select(static item => item.CompletedAtUtcTicks!.Value - item.StartedAtUtcTicks!.Value);
        var duration = await GetDurationStatisticsAsync(durationTicks, cancellationToken);
        var trend = await GetTrendAsync(
            completions,
            range,
            startTicks,
            cancellationToken);
        var topByVolume = await GetJobRankingAsync(
            completions,
            query.TopJobLimit,
            AnalyticsJobRanking.Volume,
            cancellationToken);
        var topByFailures = await GetJobRankingAsync(
            completions,
            query.TopJobLimit,
            AnalyticsJobRanking.Failures,
            cancellationToken);
        var measurableCompletions = completions
            .Where(static execution =>
                execution.StartedAtUtcTicks != null
                && execution.CompletedAtUtcTicks >= execution.StartedAtUtcTicks);
        // Slowest execution per job in two phases: a plain GROUP BY + MAX reduces each job to its peak duration,
        // then an exact-equality fetch loads all maximum-duration ties. Choose one ordinal instance winner per job
        // before global ranking and limiting. Correlated argmax subqueries are not translated reliably by every provider.
        var slowestBoundaries = await measurableCompletions
            .GroupBy(static execution => new { execution.OwnerKey, execution.JobKey })
            .Select(static group => new
            {
                group.Key.OwnerKey,
                group.Key.JobKey,
                DurationTicks = group.Max(static execution =>
                    execution.CompletedAtUtcTicks!.Value - execution.StartedAtUtcTicks!.Value)
            })
            .ToArrayAsync(cancellationToken);
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
        IQueryable<long> durationTicks,
        CancellationToken cancellationToken)
    {
        var count = await durationTicks.LongCountAsync(cancellationToken);
        if (count == 0)
        {
            return new JobExecutionDurationStatistics();
        }

        var ordered = durationTicks.Order();
        var minimum = await ordered.FirstAsync(cancellationToken);
        var maximum = await ordered.OrderDescending().FirstAsync(cancellationToken);
        var average = await durationTicks.AverageAsync(cancellationToken);
        var p50 = await GetPercentileAsync(ordered, count, 0.50, cancellationToken);
        var p90 = await GetPercentileAsync(ordered, count, 0.90, cancellationToken);
        var p95 = await GetPercentileAsync(ordered, count, 0.95, cancellationToken);
        var p99 = await GetPercentileAsync(ordered, count, 0.99, cancellationToken);
        return new JobExecutionDurationStatistics
        {
            Count = count,
            Minimum = TimeSpan.FromTicks(minimum),
            Average = TimeSpan.FromTicks(checked((long)average)),
            P50 = TimeSpan.FromTicks(p50),
            P90 = TimeSpan.FromTicks(p90),
            P95 = TimeSpan.FromTicks(p95),
            P99 = TimeSpan.FromTicks(p99),
            Maximum = TimeSpan.FromTicks(maximum)
        };
    }

    private static Task<long> GetPercentileAsync(
        IOrderedQueryable<long> ordered,
        long count,
        double percentile,
        CancellationToken cancellationToken)
    {
        var index = checked((int)Math.Max(0, Math.Ceiling(percentile * count) - 1));
        return ordered.Skip(index).FirstAsync(cancellationToken);
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

    private static async Task<IReadOnlyList<JobExecutionAnalyticsJobRank>> GetJobRankingAsync(
        IQueryable<JobExecutionEntity> completions,
        int limit,
        AnalyticsJobRanking ranking,
        CancellationToken cancellationToken)
    {
        var rankedIdentities = ranking switch
        {
            AnalyticsJobRanking.Volume => await completions
                .GroupBy(static item => new { item.OwnerKey, item.JobKey })
                .Select(static group => new
                {
                    group.Key.OwnerKey,
                    group.Key.JobKey,
                    Metric = group.LongCount()
                })
                .OrderByDescending(static item => item.Metric)
                .ThenBy(static item => item.OwnerKey)
                .ThenBy(static item => item.JobKey)
                .Take(limit)
                .ToArrayAsync(cancellationToken),
            AnalyticsJobRanking.Failures => await completions
                .Where(static item => item.State == JobExecutionState.Failed)
                .GroupBy(static item => new { item.OwnerKey, item.JobKey })
                .Select(static group => new
                {
                    group.Key.OwnerKey,
                    group.Key.JobKey,
                    Metric = group.LongCount()
                })
                .OrderByDescending(static item => item.Metric)
                .ThenBy(static item => item.OwnerKey)
                .ThenBy(static item => item.JobKey)
                .Take(limit)
                .ToArrayAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(ranking), ranking, "Analytics ranking is not supported.")
        };
        if (rankedIdentities.Length == 0)
        {
            return [];
        }

        var identities = rankedIdentities
            .Select(static item => new JobId(item.OwnerKey, item.JobKey))
            .ToArray();
        var identityPredicate = CreateIdentityPredicate<JobExecutionEntity>(identities);
        var rows = await completions
            .Where(identityPredicate)
            .GroupBy(static item => new { item.OwnerKey, item.JobKey })
            .Select(static group => new AnalyticsJobRankRow(
                group.Key.OwnerKey,
                group.Key.JobKey,
                group.LongCount(item => item.State == JobExecutionState.Succeeded),
                group.LongCount(item => item.State == JobExecutionState.Failed),
                group.LongCount(item => item.State == JobExecutionState.Skipped),
                group.LongCount(item => item.State == JobExecutionState.Cancelled)))
            .ToArrayAsync(cancellationToken);
        var templateBoundaries = await completions
            .Where(identityPredicate)
            .GroupBy(static item => new { item.OwnerKey, item.JobKey })
            .Select(static group => new
            {
                group.Key.OwnerKey,
                group.Key.JobKey,
                CompletedAtUtcTicks = group.Max(static item => item.CompletedAtUtcTicks!.Value)
            })
            .ToArrayAsync(cancellationToken);
        var latestTemplateRows = await completions
            .Where(identityPredicate)
            .Where(CreateBoundaryPredicate(
                templateBoundaries.Select(static row =>
                    (new JobId(row.OwnerKey, row.JobKey), row.CompletedAtUtcTicks)),
                static item => item.CompletedAtUtcTicks!.Value))
            .Select(static item => new
            {
                item.OwnerKey,
                item.JobKey,
                CompletedAtUtcTicks = item.CompletedAtUtcTicks!.Value,
                item.InstanceId,
                item.TemplateJson
            })
            .ToArrayAsync(cancellationToken);
        var templateByIdentity = latestTemplateRows
            .GroupBy(static item => new JobId(item.OwnerKey, item.JobKey))
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .OrderByDescending(static item => item.CompletedAtUtcTicks)
                    .ThenBy(static item => item.InstanceId)
                    .First()
                    .TemplateJson);
        var rowsByIdentity = rows.ToDictionary(
            static item => new JobId(item.OwnerKey, item.JobKey),
            EqualityComparer<JobId>.Default);
        return rankedIdentities.Select(identity => new JobExecutionAnalyticsJobRank
        {
            JobName = Deserialize<JobExecutionTemplate>(
                templateByIdentity[new JobId(identity.OwnerKey, identity.JobKey)]).JobName,
            OwnerKey = identity.OwnerKey,
            JobKey = identity.JobKey,
            SucceededCount = rowsByIdentity[new JobId(identity.OwnerKey, identity.JobKey)].SucceededCount,
            FailedCount = rowsByIdentity[new JobId(identity.OwnerKey, identity.JobKey)].FailedCount,
            SkippedCount = rowsByIdentity[new JobId(identity.OwnerKey, identity.JobKey)].SkippedCount,
            CancelledCount = rowsByIdentity[new JobId(identity.OwnerKey, identity.JobKey)].CancelledCount
        }).ToArray();
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private sealed record AnalyticsStateCount(JobExecutionState State, long Count);

    private sealed record AnalyticsOutcomeCount(
        JobExecutionState State,
        JobExecutionOrigin Origin,
        long Count);

    private sealed record AnalyticsTrendRow(
        long BucketIndex,
        long SucceededCount,
        long FailedCount,
        long SkippedCount,
        long CancelledCount,
        long ExecutedTerminalCount);

    private sealed record AnalyticsJobRankRow(
        string OwnerKey,
        string JobKey,
        long SucceededCount,
        long FailedCount,
        long SkippedCount,
        long CancelledCount);

    private enum AnalyticsJobRanking
    {
        Volume,
        Failures
    }
}
