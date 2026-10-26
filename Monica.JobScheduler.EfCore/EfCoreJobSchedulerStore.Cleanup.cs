using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.EfCore;

public sealed partial class EfCoreJobSchedulerStore
{
    /// <inheritdoc />
    public async Task<JobHistoryCleanupResult> CleanupHistoryAsync(
        JobHistoryCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCleanupRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        var jobs = await ReadAsync(async (context, token) =>
        {
            var terminal = TerminalExecutions(context, request.SchedulerScopeKey);
            if (request.AfterJob is { } after)
            {
                terminal = terminal.Where(item => string.Compare(item.OwnerKey, after.OwnerKey) > 0
                    || (item.OwnerKey == after.OwnerKey && string.Compare(item.JobKey, after.JobKey) > 0));
            }

            return await terminal.Select(item => new { item.OwnerKey, item.JobKey })
                .Distinct()
                .OrderBy(item => item.OwnerKey)
                .ThenBy(item => item.JobKey)
                .Take(request.MaxJobs == int.MaxValue ? int.MaxValue : request.MaxJobs + 1)
                .ToArrayAsync(token);
        }, cancellationToken);

        var deleted = 0;
        var scanned = 0;
        var lastScanned = request.AfterJob;
        foreach (var identity in jobs.Take(request.MaxJobs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted >= request.MaxDeletions || elapsed.Elapsed >= request.TimeBudget)
            {
                break;
            }

            var job = new JobId(identity.OwnerKey, identity.JobKey);
            var remaining = request.MaxDeletions - deleted;
            var batch = await WriteAsync(async (context, token) =>
            {
                var definition = await context.Definitions.AsNoTracking().SingleOrDefaultAsync(item =>
                    item.SchedulerScopeKey == request.SchedulerScopeKey
                    && item.OwnerKey == job.OwnerKey && item.JobKey == job.JobKey, token);
                var effective = definition is null ? null : ToDefinition(definition).EffectiveConfiguration;
                var maxRecords = effective?.MaxRetainedHistoryRecords ?? request.MaxRetainedOrphanedExecutions;
                var terminal = TerminalExecutions(context, request.SchedulerScopeKey)
                    .Where(item => item.OwnerKey == job.OwnerKey && item.JobKey == job.JobKey);
                var peekLimit = remaining == int.MaxValue ? int.MaxValue : remaining + 1;
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                if (maxRecords > 0)
                {
                    var countCandidates = await terminal
                        .OrderByDescending(item => item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks)
                        .ThenByDescending(item => item.InstanceId)
                        .Skip(maxRecords)
                        .Select(item => item.InstanceId)
                        .Take(peekLimit)
                        .ToArrayAsync(token);
                    candidates.UnionWith(countCandidates);
                }

                if (effective?.MaxRetentionDays is { } maxDays && maxDays > 0)
                {
                    var now = await GetUtcNowAsync(context, token);
                    var cutoffTicks = ToTicks(now.AddDays(-maxDays));
                    var ageCandidates = await terminal
                        .Where(item => (item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks) < cutoffTicks)
                        .OrderBy(item => item.CompletedAtUtcTicks ?? item.CreatedAtUtcTicks)
                        .ThenBy(item => item.InstanceId)
                        .Select(item => item.InstanceId)
                        .Take(peekLimit)
                        .ToArrayAsync(token);
                    candidates.UnionWith(ageCandidates);
                }

                var selected = candidates.Take(remaining).ToArray();
                var removed = selected.Length == 0
                    ? 0
                    : await terminal.Where(item => selected.Contains(item.InstanceId)).ExecuteDeleteAsync(token);
                return new HistoryCleanupBatch(removed, candidates.Count > remaining);
            }, cancellationToken, request.CoordinatorLease);
            deleted += batch.DeletedCount;
            if (batch.HasMoreCandidates)
            {
                // The continuation names only fully processed jobs; a partial backlog must be visited again.
                break;
            }

            scanned++;
            lastScanned = job;
        }

        return new JobHistoryCleanupResult
        {
            DeletedCount = deleted,
            ScannedJobs = scanned,
            LastScannedJob = lastScanned,
            CompletedScan = jobs.Length <= request.MaxJobs && scanned == jobs.Length
        };
    }

    private static void ValidateCleanupRequest(JobHistoryCleanupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentity(request.SchedulerScopeKey, nameof(request.SchedulerScopeKey));
        request.AfterJob?.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxJobs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxDeletions, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(request.MaxRetainedOrphanedExecutions);
        if (request.TimeBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The cleanup time budget must be positive.");
        }

        request.CoordinatorLease?.ValidatePartition(request.SchedulerScopeKey, "maintenance");
    }

    private readonly record struct HistoryCleanupBatch(int DeletedCount, bool HasMoreCandidates);
}
