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
using Monica.Locker.Leases;

namespace Monica.JobScheduler.Services.Support;

/// <summary>
/// Runs owner scheduling and independent scope recovery/history loops. Durable coordinator leases suppress replica
/// duplication; each coordinated mutation validates its grant before locking job state. Maintenance includes owners
/// no longer present on any running host. Manual hosts retain their isolated catalog without background loops.
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
    private readonly LeaseCoordinator _coordinators = new(store.CoordinatorLeases,
        schedulerOptions.Value.WorkerInstanceId, timeProvider, logger, new LeaseCoordinatorOptions
        {
            Duration = schedulerOptions.Value.CoordinatorLeaseDuration,
            StandbyInterval = schedulerOptions.Value.SchedulingPollInterval,
            OperationTimeout = TimeSpan.FromTicks(Math.Min(schedulerOptions.Value.StoreOperationTimeout.Ticks,
                schedulerOptions.Value.CoordinatorLeaseDuration.Ticks / 4))
        });
    private readonly Dictionary<string, DateTimeOffset> _nextSnapshotSyncUtc = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _synchronizedGrants = new(StringComparer.Ordinal);
    private readonly object _readinessLock = new();
    private readonly Dictionary<string, bool> _readyPlanes = new()
        { ["owner scheduling"] = false, ["lease recovery"] = false, ["history cleanup"] = false };
    private JobId? _cleanupAfterJob;
    private DateTimeOffset _nextCleanupUtc;
    private int _nextMaterializationOwnerIndex;
    private bool _manualCatalogInitialized;

    public override string? ServiceGroupId => nameof(ModuleJobScheduler);

    protected override Task OnStartingAsync(CancellationToken cancellationToken) =>
        _options.AutonomousWork ? Task.CompletedTask : ScheduleAsync(cancellationToken);

    protected override async Task ExecuteBackgroundAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_options.AutonomousWork)
                await Task.WhenAll(
                    RunLoopAsync("owner scheduling", ScheduleAsync, stoppingToken),
                    RunLoopAsync("lease recovery", RecoverAsync, stoppingToken),
                    RunLoopAsync("history cleanup", CleanupHistoryAsync, stoppingToken));
        }
        finally { await _coordinators.DisposeAsync(); }
    }

    public override void Dispose()
    {
        _coordinators.Dispose();
        base.Dispose();
    }

    private async Task RunLoopAsync(string plane, Func<CancellationToken, Task> cycle, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await cycle(stoppingToken);
                bool becameReady;
                lock (_readinessLock)
                {
                    _readyPlanes[plane] = true;
                    becameReady = _readyPlanes.Values.All(static ready => ready) && runtimeState.SetScheduling(true,
                        $"Owner scheduling is active or standing by for {_localOwners.Length} logical owner(s).");
                }
                if (becameReady)
                    RecordState("Owner scheduling is active or standing by", HostedServiceState.Running);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                lock (_readinessLock)
                {
                    _readyPlanes[plane] = false;
                    _ = runtimeState.SetScheduling(false, $"{plane}: {exception.Message}");
                }
                RecordState($"Scheduler {plane} cycle failed", HostedServiceState.Degraded, exception, LogLevel.Error);
            }
            var delay = _options.SchedulingPollInterval * (0.9 + Random.Shared.NextDouble() * 0.1);
            await Task.Delay(delay, timeProvider, stoppingToken);
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
        var snapshots = GetLocalSnapshots(scopeKey);
        var budget = _options.MaxRecurringMaterializationsPerCycle;
        for (var visited = 0; visited < snapshots.Length; visited++)
        {
            var snapshot = snapshots[(_nextMaterializationOwnerIndex + visited) % snapshots.Length];
            var context = await GetCoordinatorAsync("owner:" + snapshot.OwnerKey, cancellationToken);
            if (context is null) continue;
            using var term = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StoppingToken);
            var lease = JobCoordinatorLeaseAdapter.ToKey(context.Identity);
            await SyncOwnerStateAsync(snapshot, lease, term.Token);
            if (budget > 0)
                budget = await MaterializeOwnerDueOccurrencesAsync(scopeKey, snapshot.OwnerKey, budget, lease, term.Token);
            runtimeState.RecordOwnerSchedulingWork(timeProvider.GetUtcNow());
        }
        if (snapshots.Length > 0)
            _nextMaterializationOwnerIndex = (_nextMaterializationOwnerIndex + 1) % snapshots.Length;
    }

    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutonomousWork) return;
        var context = await GetCoordinatorAsync("maintenance", cancellationToken);
        if (context is null) return;
        using var term = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StoppingToken);
        cancellationToken = term.Token;
        var lease = JobCoordinatorLeaseAdapter.ToKey(context.Identity);
        await store.RecoverExpiredLeasesAsync(new ExpiredLeaseRecoveryRequest
        {
            SchedulerScopeKey = _options.SchedulerScopeKey,
            CoordinatorLease = lease,
            MaxCount = _options.MaxExpiredLeaseRecoveriesPerCycle,
            RetryDelay = _options.ExecutionRetryDelay,
            MaxLeaseLossesBeforeFailure = _options.MaxLeaseLossesBeforeFailure
        }, cancellationToken);
        runtimeState.RecordLeaseRecoveryWork(timeProvider.GetUtcNow());
    }

    private JobOwnerSnapshot[] GetLocalSnapshots(string scopeKey)
    {
        var snapshots = _localOwners.Select(owner => new JobOwnerSnapshot(scopeKey, owner.Key,
            owner.Select(definition => definition.Declaration).ToArray())).ToList();
        var ownersWithDefinitions = _localOwners.Select(owner => owner.Key).ToHashSet(StringComparer.Ordinal);
        var emptyOwners = activation?.ActiveWorkloadOwners is not { } selectedOwners
            ? (_localOwners.Length == 0 ? new[] { _options.GetProjectName() } : [])
            : selectedOwners.Except(ownersWithDefinitions, StringComparer.Ordinal);
        snapshots.AddRange(emptyOwners.Select(owner => new JobOwnerSnapshot(scopeKey, owner, [])));
        return snapshots.OrderBy(snapshot => snapshot.OwnerKey, StringComparer.Ordinal).ToArray();
    }

    private async Task SyncOwnerStateAsync(JobOwnerSnapshot snapshot, JobCoordinatorLeaseKey lease,
        CancellationToken cancellationToken)
    {
        var newGrant = !_synchronizedGrants.TryGetValue(snapshot.OwnerKey, out var synchronizedToken)
                       || synchronizedToken != lease.LeaseToken;
        if (!newGrant && _nextSnapshotSyncUtc.TryGetValue(snapshot.OwnerKey, out var next)
            && timeProvider.GetUtcNow() < next) return;
        var result = await store.SyncOwnerSnapshotAsync(snapshot with
        {
            CoordinatorLease = lease,
            ExpectedPreviousManifestHash = _options.OwnerManifestReplacements.GetValueOrDefault(snapshot.OwnerKey)
        }, cancellationToken);
        // Identical steady-state manifests touch one owner row. Cursor reconciliation is required only after
        // content changes or a new coordinator grant (including startup and takeover).
        if (newGrant || result.ContentChanged)
        {
            foreach (var declaration in snapshot.Declarations.Where(declaration => declaration.JobType == JobType.Recurring))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await store.SynchronizeRecurringScheduleAsync(new RecurringScheduleSynchronization
                {
                    CoordinatorLease = lease,
                    CursorKey = new RecurringScheduleCursorKey
                    {
                        SchedulerScopeKey = snapshot.SchedulerScopeKey, OwnerKey = snapshot.OwnerKey, JobKey = declaration.JobKey
                    },
                    HostSuspensionReasons = JobRecurringScheduleSuspensionReason.None
                }, cancellationToken);
            }
        }
        _synchronizedGrants[snapshot.OwnerKey] = lease.LeaseToken;
        _nextSnapshotSyncUtc[snapshot.OwnerKey] = timeProvider.GetUtcNow().Add(_options.SnapshotSyncInterval);
    }

    private Task<LeaseContext?> GetCoordinatorAsync(string partition, CancellationToken cancellationToken) =>
        _coordinators.GetAsync(new LeaseResource(_options.SchedulerScopeKey,
            JobCoordinatorLeaseAdapter.PREFIX + partition), cancellationToken);

    private async Task<int> MaterializeOwnerDueOccurrencesAsync(
        string scopeKey, string ownerKey, int remainingBudget, JobCoordinatorLeaseKey lease, CancellationToken cancellationToken)
    {
        while (remainingBudget > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                var result = await store.TryMaterializeRecurringOccurrenceAsync(
                    new RecurringOccurrenceMaterialization
                    {
                        CoordinatorLease = lease,
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

    internal async Task CleanupHistoryAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (!_options.AutonomousWork || !_options.EnableHistoryCleanup || now < _nextCleanupUtc) return;
        var context = await GetCoordinatorAsync("maintenance", cancellationToken);
        if (context is null) return;
        using var term = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StoppingToken);
        cancellationToken = term.Token;
        var lease = JobCoordinatorLeaseAdapter.ToKey(context.Identity);
        var result = await store.CleanupHistoryAsync(new JobHistoryCleanupRequest
        {
            SchedulerScopeKey = _options.SchedulerScopeKey, CoordinatorLease = lease,
            AfterJob = _cleanupAfterJob, MaxDeletions = _options.MaxHistoryDeletionsPerCycle,
            MaxRetainedOrphanedExecutions = _options.MaxRetainedOrphanedExecutions
        }, cancellationToken);
        _cleanupAfterJob = result.CompletedScan ? null : result.LastScannedJob;
        runtimeState.RecordHistoryCleanupWork(timeProvider.GetUtcNow());
        _nextCleanupUtc = result.CompletedScan ? now.Add(_options.HistoryCleanupInterval) : now;
        if (result.CompletedScan)
        {
            Logger.LogInformation(
                "Scheduler history cleanup scan completed for scope {SchedulerScopeKey} by {WorkerInstanceId}; next scan at {NextCleanupUtc}.",
                _options.SchedulerScopeKey, _options.WorkerInstanceId, _nextCleanupUtc);
        }
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
