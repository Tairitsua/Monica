using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monica.Core.HostedService.Abstractions;
using Monica.Core.HostedService.Models;
using Monica.Core.ObservableInstance.Abstractions;
using Monica.Core.TypeDiscovery.Abstractions;
using Monica.JobScheduler.Abstractions;
using Monica.JobScheduler.Models;
using Monica.JobScheduler.Models.Definitions;
using Monica.JobScheduler.Models.Execution;
using Monica.Modules;

namespace Monica.JobScheduler.Services.Support;

/// <summary>
/// Schedules this host's selected jobs: it publishes each logical owner's snapshot, materializes due recurring
/// occurrences, recovers expired execution leases, and cleans up terminal history. Every operation is an idempotent
/// database compare-and-swap, so all replicas of one owner may run this loop concurrently, and every host may run it
/// without any deployment coordination.
/// </summary>
internal sealed class JobSchedulingHostedService(
    IJobSchedulerStore store,
    IReadOnlyList<LocalJobDefinition> localDefinitions,
    JobSchedulerRuntimeState runtimeState,
    TimeProvider timeProvider,
    IOptions<ModuleJobSchedulerOption> schedulerOptions,
    IObservableInstanceRegistry observableRegistry,
    IOptions<ModuleHostedServiceOption> hostedServiceOptions,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<JobSchedulingHostedService> logger,
    ITypeActivationPolicy? activation = null)
    : MoBackgroundService(observableRegistry, hostedServiceOptions, serviceScopeFactory, logger)
{
    private readonly ModuleJobSchedulerOption _options = schedulerOptions.Value;
    private readonly IGrouping<string, LocalJobDefinition>[] _localOwners = localDefinitions
        .GroupBy(definition => definition.OwnerKey ?? schedulerOptions.Value.GetProjectName(), StringComparer.Ordinal)
        .OrderBy(static owner => owner.Key, StringComparer.Ordinal)
        .ToArray();
    private DateTimeOffset _nextSnapshotSyncUtc;
    private DateTimeOffset _nextCleanupUtc;
    private int _nextMaterializationOwnerIndex;
    private bool _manualCatalogInitialized;

    public override string? ServiceGroupId => nameof(ModuleJobScheduler);

    protected override Task OnStartingAsync(CancellationToken cancellationToken) =>
        _options.AutonomousWork ? Task.CompletedTask : ScheduleAsync(cancellationToken);

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutonomousWork) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScheduleAsync(stoppingToken);
                if (runtimeState.SetScheduling(
                        true,
                        $"Scheduling is active for {_localOwners.Length} logical owner(s) with {localDefinitions.Count} local job(s)."))
                {
                    RecordState(
                        $"Scheduler active for {_localOwners.Length} logical owner(s)",
                        HostedServiceState.Running);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _ = runtimeState.SetScheduling(false, exception.Message);
                RecordState(
                    "Scheduler scheduling cycle failed",
                    HostedServiceState.Degraded,
                    exception,
                    LogLevel.Error);
            }

            await Task.Delay(_options.SchedulingPollInterval, timeProvider, stoppingToken);
        }
    }

    internal async Task ScheduleAsync(CancellationToken cancellationToken)
    {
        var scopeKey = _options.SchedulerScopeKey;
        if (!_options.AutonomousWork)
        {
            if (_manualCatalogInitialized) return;
            foreach (var owner in _localOwners)
            {
                await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(scopeKey, owner.Key,
                    owner.Select(definition => definition.Declaration).ToArray()), cancellationToken);
                foreach (var definition in owner.Where(definition => definition.Declaration.JobType == JobType.Recurring))
                    await store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
                    {
                        CursorKey = new RecurringScheduleCursorKey
                        {
                            SchedulerScopeKey = scopeKey, OwnerKey = owner.Key, JobKey = definition.Declaration.JobKey
                        },
                        HostSuspensionReasons = JobRecurringScheduleSuspensionReason.Manual
                    }, cancellationToken);
            }
            _manualCatalogInitialized = true;
            runtimeState.SetScheduling(true, "The isolated local catalog is ready; automatic scheduling and maintenance are held.");
            return;
        }
        await SyncOwnerStateAsync(scopeKey, cancellationToken);
        await store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = scopeKey,
            MaxCount = _options.MaxExpiredLeaseRecoveriesPerCycle,
            RetryDelay = _options.ExecutionRetryDelay,
            MaxLeaseLossesBeforeFailure = _options.MaxLeaseLossesBeforeFailure
        }, cancellationToken);
        await MaterializeDueOccurrencesAsync(scopeKey, cancellationToken);
        await CleanupHistoryAsync(scopeKey, cancellationToken);
    }

    private async Task SyncOwnerStateAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextSnapshotSyncUtc)
        {
            return;
        }

        var ownersWithDefinitions = _localOwners.Select(owner => owner.Key).ToHashSet(StringComparer.Ordinal);
        var emptyOwners = activation?.ActiveWorkloadOwners is not { } selectedOwners
            ? (_localOwners.Length == 0 ? new[] { _options.GetProjectName() } : [])
            : selectedOwners.Except(ownersWithDefinitions, StringComparer.Ordinal);
        foreach (var emptyOwner in emptyOwners)
        {
            // A selected role can remove its last job. Inactive roles must never publish retirement snapshots.
            await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
                scopeKey, emptyOwner, []), cancellationToken);
        }

        // Reconcile host-owned suspension reasons with the same cadence as the owner snapshot. Policy edits update
        // their cursor synchronously; this pass is only needed at startup and after a definition snapshot changes.
        const JobRecurringScheduleSuspensionReason hostReasons = JobRecurringScheduleSuspensionReason.None;
        foreach (var owner in _localOwners)
        {
            await store.SyncOwnerSnapshotAsync(new JobOwnerSnapshot(
                scopeKey,
                owner.Key,
                owner.Select(static definition => definition.Declaration).ToArray()), cancellationToken);
            foreach (var definition in owner.Where(static definition =>
                         definition.Declaration.JobType == JobType.Recurring))
            {
                await store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
                {
                    CursorKey = new RecurringScheduleCursorKey
                    {
                        SchedulerScopeKey = scopeKey,
                        OwnerKey = owner.Key,
                        JobKey = definition.Declaration.JobKey
                    },
                    HostSuspensionReasons = hostReasons
                }, cancellationToken);
            }
        }

        // Advance only after all owner snapshots and cursor synchronizations succeed. A partial failure retries
        // the complete selected catalog on the next scheduling cycle.
        _nextSnapshotSyncUtc = timeProvider.GetUtcNow().Add(_options.SnapshotSyncInterval);
    }

    private async Task MaterializeDueOccurrencesAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var remainingBudget = _options.MaxRecurringMaterializationsPerCycle;
        for (var visited = 0; visited < _localOwners.Length && remainingBudget > 0; visited++)
        {
            var owner = _localOwners[_nextMaterializationOwnerIndex];
            _nextMaterializationOwnerIndex = (_nextMaterializationOwnerIndex + 1) % _localOwners.Length;
            remainingBudget = await MaterializeOwnerDueOccurrencesAsync(
                scopeKey, owner.Key, remainingBudget, cancellationToken);
        }
    }

    private async Task<int> MaterializeOwnerDueOccurrencesAsync(
        string scopeKey, string ownerKey, int remainingBudget, CancellationToken cancellationToken)
    {
        while (remainingBudget > 0)
        {
            var cursors = await store.GetDueRecurringSchedulesAsync(
                scopeKey,
                ownerKey,
                remainingBudget,
                cancellationToken);
            if (cursors.Count == 0)
            {
                return remainingBudget;
            }

            var materializedThisRound = 0;
            foreach (var cursor in cursors)
            {
                if (cursor.NextOccurrenceUtc is not { } occurrence)
                {
                    continue;
                }

                // The store owns the authoritative clock and advances the cursor past all missed occurrences in the
                // same atomic operation as enqueueing this one. The host must not calculate the next cursor value from
                // a potentially skewed process clock.
                var result = await store.TryMaterializeRecurringOccurrenceAsync(
                    new RecurringOccurrenceMaterialization
                    {
                        CursorKey = cursor.Key,
                        ExpectedVersion = cursor.Version,
                        ExpectedOccurrenceUtc = occurrence,
                        InstanceId = CreateRecurringInstanceId(cursor.Key, occurrence)
                    },
                    cancellationToken);
                if (result.Status != RecurringMaterializationStatus.Materialized)
                {
                    continue;
                }

                remainingBudget--;
                materializedThisRound++;
                if (remainingBudget == 0)
                {
                    return remainingBudget;
                }
            }

            // Another replica may have advanced every observed cursor. Re-querying without local progress would spin
            // against stale snapshots, so leave the refreshed durable cursors for the next scheduling cycle.
            if (materializedThisRound == 0)
            {
                return remainingBudget;
            }
        }

        return remainingBudget;
    }

    private async Task CleanupHistoryAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!_options.EnableHistoryCleanup || now < _nextCleanupUtc)
        {
            return;
        }

        var policies = new Dictionary<JobId, JobHistoryRetentionPolicy>();
        var pageNumber = 1;
        while (true)
        {
            var page = await store.QueryDefinitionsAsync(
                scopeKey,
                new JobDefinitionQuery { PageNumber = pageNumber, PageSize = 500 },
                cancellationToken);
            foreach (var definition in page.Items)
            {
                var effective = definition.EffectiveConfiguration;
                policies[new JobId(definition.OwnerKey, definition.Declaration.JobKey)] = new JobHistoryRetentionPolicy
                {
                    MaxRecords = effective.MaxRetainedHistoryRecords,
                    MaxDays = effective.MaxRetentionDays
                };
            }

            if (page.Items.Count < 500)
            {
                break;
            }

            pageNumber++;
        }

        var candidates = await store.GetExecutionCleanupCandidatesAsync(
            scopeKey,
            policies,
            _options.MaxRetainedOrphanedExecutions,
            _options.MaxHistoryDeletionsPerCycle,
            cancellationToken);
        await store.DeleteExecutionsAsync(scopeKey, candidates, cancellationToken);
        // A full batch proves more eligible history may remain. Drain another bounded batch on the next scheduling
        // cycle; use the configured interval only after a partial batch establishes that the backlog is drained.
        _nextCleanupUtc = candidates.Count == _options.MaxHistoryDeletionsPerCycle
            ? now
            : now.Add(_options.HistoryCleanupInterval);
    }

    internal static string CreateRecurringInstanceId(
        RecurringScheduleCursorKey key,
        DateTimeOffset occurrence)
    {
        var payload = new StringBuilder()
            .AppendLengthPrefixed(key.SchedulerScopeKey)
            .AppendLengthPrefixed(key.OwnerKey)
            .AppendLengthPrefixed(key.JobKey)
            .Append('|')
            .Append(occurrence.UtcTicks.ToString(CultureInfo.InvariantCulture))
            .ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

file static class StringBuilderExtensions
{
    public static StringBuilder AppendLengthPrefixed(this StringBuilder builder, string value)
    {
        return builder.Append(value.Length).Append(':').Append(value);
    }
}
