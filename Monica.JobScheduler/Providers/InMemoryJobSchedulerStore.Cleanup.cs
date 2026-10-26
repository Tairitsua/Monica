using System.Diagnostics;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Execution;

namespace Monica.JobScheduler.Providers;

public sealed partial class InMemoryJobSchedulerStore
{
    /// <inheritdoc />
    public Task<JobHistoryCleanupResult> CleanupHistoryAsync(
        JobHistoryCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateCleanupRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        JobId[] jobs;
        lock (_gate)
        {
            jobs = _executionInstances.Values
                .Where(item => item.Template.SchedulerScopeKey == request.SchedulerScopeKey && IsTerminal(item.State))
                .Select(item => new JobId(item.Template.OwnerKey, item.Template.JobKey))
                .Distinct()
                .Where(job => request.AfterJob is not { } after
                    || string.CompareOrdinal(job.OwnerKey, after.OwnerKey) > 0
                    || (job.OwnerKey == after.OwnerKey && string.CompareOrdinal(job.JobKey, after.JobKey) > 0))
                .OrderBy(job => job.OwnerKey, StringComparer.Ordinal)
                .ThenBy(job => job.JobKey, StringComparer.Ordinal)
                .Take(request.MaxJobs == int.MaxValue ? int.MaxValue : request.MaxJobs + 1)
                .ToArray();
        }

        var deleted = 0;
        var scanned = 0;
        var lastScanned = request.AfterJob;
        foreach (var job in jobs.Take(request.MaxJobs))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted >= request.MaxDeletions || elapsed.Elapsed >= request.TimeBudget)
            {
                break;
            }

            lock (_gate)
            {
                ValidateCoordinatorLeaseUnsafe(request.CoordinatorLease);
                var effective = _definitions.TryGetValue(
                    new DefinitionKey(request.SchedulerScopeKey, job.OwnerKey, job.JobKey), out var definition)
                    ? definition.EffectiveConfiguration
                    : null;
                var maxRecords = effective?.MaxRetainedHistoryRecords ?? request.MaxRetainedOrphanedExecutions;
                var remaining = request.MaxDeletions - deleted;
                var peekLimit = remaining == int.MaxValue ? int.MaxValue : remaining + 1;
                var terminal = _executionInstances.Values.Where(item =>
                    item.Template.SchedulerScopeKey == request.SchedulerScopeKey
                    && item.Template.OwnerKey == job.OwnerKey && item.Template.JobKey == job.JobKey
                    && IsTerminal(item.State));
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                if (maxRecords > 0)
                {
                    candidates.UnionWith(terminal
                        .OrderByDescending(item => item.CompletedAtUtc ?? item.CreatedAtUtc)
                        .ThenByDescending(item => item.InstanceId, StringComparer.Ordinal)
                        .Skip(maxRecords)
                        .Take(peekLimit)
                        .Select(item => item.InstanceId));
                }

                if (effective?.MaxRetentionDays is { } maxDays && maxDays > 0)
                {
                    var cutoff = UtcNow.AddDays(-maxDays);
                    candidates.UnionWith(terminal
                        .Where(item => (item.CompletedAtUtc ?? item.CreatedAtUtc) < cutoff)
                        .OrderBy(item => item.CompletedAtUtc ?? item.CreatedAtUtc)
                        .ThenBy(item => item.InstanceId, StringComparer.Ordinal)
                        .Take(peekLimit)
                        .Select(item => item.InstanceId));
                }

                var partial = candidates.Count > remaining;
                foreach (var instanceId in candidates.Take(remaining))
                {
                    var key = new ExecutionKey(request.SchedulerScopeKey, instanceId);
                    if (_executionInstances.TryGetValue(key, out var execution) && IsTerminal(execution.State)
                        && _executionInstances.Remove(key))
                    {
                        deleted++;
                    }
                }

                if (partial)
                {
                    break;
                }

                scanned++;
                lastScanned = job;
            }
        }

        return Task.FromResult(new JobHistoryCleanupResult
        {
            DeletedCount = deleted,
            ScannedJobs = scanned,
            LastScannedJob = lastScanned,
            CompletedScan = jobs.Length <= request.MaxJobs && scanned == jobs.Length
        });
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
}
